# LSM Tree (Log-Structured Merge Tree) — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

You rarely "use an LSM tree." You inherit one. The moment your team picks Cassandra, ScyllaDB, HBase, or any RocksDB-backed system (CockroachDB, TiKV, YugabyteDB, MyRocks, Kafka Streams' state stores, Flink's RocksDB backend), every operational quirk in this document becomes your problem. The LSM is the silent layer between the database's query engine and the block device, and it leaks its trade-offs upward as latency spikes, disk-full pages, and CPU you can't explain.

In a metrics or observability platform — Prometheus-style TSDBs, InfluxDB, anything ingesting a firehose of points — the LSM (or an LSM-like append-and-compact design) is the ingestion engine. It is why you can take millions of writes per second on commodity SSDs, and why your 3 a.m. page says "disk at 95%, compaction backlog growing."

In a NewSQL or wide-column store backing user-facing features, RocksDB sits under each shard. Your p99 read latency, your write-stall incidents, and your storage bill all trace back to how its memtables, compaction threads, and Bloom filters are tuned — settings most teams never touch until something breaks.

The recurring shape: writes are cheap and you fall in love with the system, then weeks later compaction, tombstones, or space amplification present a bill you didn't budget for. This doc is about seeing that bill coming.

## Best practices

### 1. Match the compaction strategy to the read/write/delete shape, not the default
**Do:** Leveled (LCS / RocksDB leveled) for read-heavy and update-heavy tables; size-tiered (STCS / universal) for write-heavy, append-mostly tables; time-windowed (TWCS) for TTL'd time-series.
**Why:** The strategy *is* the read-vs-write-vs-space amplification dial. Wrong strategy means either a read p99 that fans out across dozens of SSTables, or write amplification north of 10x burning your SSD endurance.
**Avoid:** Leaving Cassandra on the STCS default for a heavily updated, read-latency-sensitive table — reads scan many overlapping tiers.

### 2. Use TWCS for anything with a uniform TTL, and only that
**Do:** Pick `compaction_window_size` so the TTL spans roughly 20–30 windows (90-day TTL → ~3-day windows). Whole expired SSTables get dropped without rewriting data.
**Why:** Space is reclaimed by deleting files, not by merging tombstones — the cheapest possible reclaim. STCS/LCS would rewrite expiring data repeatedly.
**Avoid:** TWCS on a table that does explicit deletes of non-TTL'd rows; tombstones and the data they shadow land in different time windows and never compact together.

### 3. Size memtables and flush/compaction parallelism so compaction keeps up with ingest
**Do:** Watch L0 file count and pending-compaction bytes. If they climb under steady load, raise `max_background_jobs` (more compaction threads) and `max_write_buffer_number` (more memtables to absorb bursts).
**Why:** When L0 overflows faster than background workers drain it, RocksDB throttles then stops writers — a write stall that shows up as a latency cliff, not a clean error.
**Avoid:** A single compaction thread on a multi-core box with SSDs that can absorb far more concurrent I/O.

### 4. Size Bloom filters at ~10 bits/key and keep them resident
**Do:** Default to 10 bits per key (~1% false-positive rate). Enable `whole_key_filtering` for point gets; add a prefix extractor only if you do prefix range scans. Consider the Ribbon filter for ~30% memory savings at the same FP rate when CPU is cheaper than RAM.
**Why:** Below ~5 bits/key, false positives turn negative lookups into wasted block reads — read amplification you'll see as elevated p99 on keys that don't even exist.
**Avoid:** Disabling filters to "save memory," then wondering why every miss hits disk.

### 5. Provision disk for transient space amplification, not just logical size
**Do:** Budget 2x logical size as a working assumption for STCS (a large tier merge needs room for the inputs plus the output simultaneously); leveled is tighter (~1.1x) but spikier under backlog.
**Why:** Compaction writes the merged output *before* deleting the inputs. Run a disk too close to full and a single big compaction wedges the node — it can neither compact nor free space.
**Avoid:** `nodetool compact` on an STCS table in production — it fuses everything into one giant SSTable with no same-size peer to ever merge with again.

### 6. Treat deletes as expensive writes
**Do:** Prefer TTLs over explicit deletes; model data so you delete whole partitions/SSTables rather than scattered cells. Tune `gc_grace_seconds` deliberately (Cassandra default 10 days exists for tombstone propagation, not performance).
**Why:** A tombstone is a live row until both it and every shadowed copy are compacted together *and* the grace period passes. Until then, reads and range scans must read past every tombstone.
**Avoid:** Queue-on-Cassandra patterns (insert, read, delete) — they manufacture tombstone storms that eventually fail reads.

### 7. Never range-scan across a wall of tombstones
**Do:** Keep `tombstone_warn_threshold` (1000) and `tombstone_failure_threshold` (100000) as guardrails, and design partitions so a scan never crosses thousands of deleted cells.
**Why:** Tombstones can't be paged — the coordinator must hold all encountered ones in memory because other replicas may not know about the delete. Latency and heap grow with tombstone count until the query trips the failure threshold.
**Avoid:** Reading "all undeleted rows in a partition" that's mostly deletes; that scan reads every tombstone first.

### 8. Tune the write-stall thresholds intentionally and alert before them
**Do:** Set `soft_pending_compaction_bytes_limit` / `hard_pending_compaction_bytes_limit` knowing the soft limit slows writers and the hard limit stops them. Alert on the *soft* limit so you act before the hard one.
**Why:** Hitting the hard limit is a full write stall — to the application it looks like the database hung. The soft limit is your early warning.
**Avoid:** Discovering these knobs exist only while reading the LOG file during the incident.

### 9. Reduce write amplification before adding hardware
**Do:** Anything that lowers write amplification (larger memtables, fewer levels, better compression, right-sized level multiplier — RocksDB default 10) speeds compaction, because compaction *is* the bytes being rewritten.
**Why:** Compaction backlog is usually a write-amplification problem wearing an I/O-saturation costume; throwing disks at it without fixing amp just delays the next stall.
**Avoid:** Scaling out the cluster to mask a single-node tuning problem.

## Anti-patterns to recognize

- **LSM as a queue or workqueue**: insert task, poll, delete done-task. It looks like a table but generates a tombstone per delete; reads slow to a crawl and eventually fail at the tombstone threshold. Use an actual queue (Kafka, SQS, Redis streams).
- **Read-heavy point lookups on a cold working set**: choosing an LSM store for a key-value cache whose working set exceeds RAM. Each miss fans out across SSTables where a B-tree would do one seek. Use a B-tree engine (InnoDB/Postgres) or an actual cache.
- **Manual major compaction "to clean things up"**: running a full compaction on an STCS table merges everything into one monster SSTable that never compacts again, so space never recovers normally. Let the strategy do its job, or switch to LCS/TWCS first.
- **Disabling Bloom filters to save RAM**: saves a little memory, then turns every nonexistent-key lookup into disk I/O. Keep filters; shrink bits/key or switch to Ribbon if memory is truly tight.
- **One TTL per row, many different TTLs per table, on TWCS**: breaks TWCS's "drop the whole expired SSTable" optimization because windows never fully expire. Use a single table-level TTL.
- **Ignoring space amplification until the disk fills**: logical data is 400 GB, you provisioned 500 GB, then a tier merge needs another 400 GB transiently and the node wedges. Size for the strategy's worst-case amp.
- **Treating compaction CPU as a leak**: seeing constant background CPU/IO and "optimizing" it away by throttling compaction — which just moves the cost to read amplification and a future write stall.

## Real-world usage patterns

**Metrics platform, 10s of millions of points/sec.** A TSDB ingesting from thousands of agents stores each metric with a fixed retention TTL. The storage layer uses time-windowed compaction so an entire window's data expires and its files are deleted as a unit. Non-obvious lesson: retention changes are nearly free (drop old windows) but *re-tuning the window size after the fact* is painful — windows already on disk keep their old boundaries, so you live with the original choice for one full retention period.

**Wide-column store behind a social feed.** A Cassandra/Scylla cluster holds per-user timelines, append-heavy with occasional edits, on STCS. The team's read p99 quietly degraded over months as overlapping tiers accumulated. The fix wasn't more nodes — it was moving the read-hot tables to leveled compaction so a point read touches at most one SSTable per level. Lesson: amplification trade-offs drift with data volume; a strategy that was fine at launch can be wrong at 10x the data.

**NewSQL OLTP on RocksDB.** A distributed SQL database (CockroachDB-style) puts a RocksDB/Pebble instance under each range. A batch import saturated compaction on a few hot ranges and triggered write stalls that the app saw as transaction timeouts. Lesson: in a sharded LSM system, you don't have one compaction problem — you have N, and a hot shard stalls independently of a healthy cluster average. Per-shard metrics, not cluster averages, find it.

**Kafka Streams / Flink state store.** A stream processor keeps windowed aggregation state in an embedded RocksDB. Under high cardinality the state store's compaction competed with the processing threads for disk and CPU, raising end-to-end stream lag. Lesson: when the LSM is co-located with compute, compaction is not "background" — it directly steals from your hot path, so cap its resource use explicitly.

## Operational checklist

- **Monitoring:** Are you tracking L0 file count, pending-compaction bytes, compaction throughput vs ingest rate, and read/write/space amplification — *per shard*, not just cluster-wide?
- **Write stalls:** Do you alert on the soft pending-compaction limit (early warning) before the hard limit (full stall) is hit?
- **Tombstones:** For delete/TTL tables, is tombstone-per-read count monitored and well under `tombstone_failure_threshold`? Is `gc_grace_seconds` set deliberately?
- **Disk headroom:** Is free space sized for the chosen strategy's transient amplification (≈2x logical for STCS) so a big compaction can't wedge the node?
- **Failure handling:** Have you tested what happens when ingest outpaces compaction for an hour? Does the system degrade gracefully or fall off a cliff?
- **Bloom filters:** Are filters enabled, sized ~10 bits/key, and resident in memory for open files?
- **Range scans:** Have you load-tested range scans (which can't use Bloom filters) separately from point reads?
- **Cost:** Do you know your write amplification factor? It directly drives SSD wear and, on cloud block storage, your IOPS/throughput bill.
- **Onboarding:** Can a new on-call engineer find the compaction-stats command (`nodetool compactionstats`, RocksDB LOG) and read the L0/backlog signals on day one?

## How this topic typically evolves in a codebase

Teams almost always start by accepting defaults. The system ingests beautifully, writes are cheap, everyone's happy, and the LSM is invisible. This honeymoon lasts exactly as long as the data is small relative to RAM and disk. Nobody tunes compaction because nothing is broken yet.

The first painful migration point arrives at scale: read p99 creeps up (overlapping SSTables under STCS), or the disk fills faster than logical growth explains (space amplification and tombstones), or ingest bursts trigger write stalls that look like outages. This is when teams discover compaction strategies exist, learn the difference between leveled and size-tiered the hard way, and often have to switch strategy on live tables — an expensive, I/O-heavy rewrite they schedule for off-peak. Delete-heavy workloads hit the tombstone wall around the same time and sometimes require a data-model redesign, not just a config change.

Mature usage looks like treating amplification as a first-class, monitored budget: per-table compaction strategies chosen on purpose, alerts on backlog and stalls, disk provisioned for worst-case amp, and a standing understanding that compaction CPU/IO is a permanent line item, not a bug. The teams that get there earliest are the ones who measured their own write amplification on day one instead of trusting the rule of thumb.

## Further reading

- [RocksDB Tuning Guide](https://github.com/facebook/rocksdb/wiki/RocksDB-Tuning-Guide) — the canonical reference for memtable sizing, compaction, and amplification knobs, written by the people who run it at scale.
- [RocksDB Write Stalls](https://github.com/facebook/rocksdb/wiki/Write-Stalls) — exactly what triggers stalls and which option moves which limit; read this *before* the incident, not during.
- [TWCS — how it works and when to use it (The Last Pickle)](https://thelastpickle.com/blog/2016/12/08/TWCS-part1.html) — the clearest explanation of time-windowed compaction and its deletes-break-it caveat.
- [Managing Tombstones in Apache Cassandra (Instaclustr)](https://www.instaclustr.com/support/documentation/cassandra/using-cassandra/managing-tombstones-in-cassandra/) — practical tombstone diagnosis and the queue-anti-pattern, from people who operate large fleets.
- [Constructing and Analyzing the LSM Compaction Design Space (VLDB 2021)](https://vldb.org/pvldb/vol14/p2216-sarkar.pdf) — the rigorous map of the compaction trade-off space behind the rules of thumb here.
- [Ribbon Filter (RocksDB blog)](https://rocksdb.org/blog/2021/12/29/ribbon-filter.html) — when to trade CPU for ~30% less filter memory than a Bloom filter at the same false-positive rate.
