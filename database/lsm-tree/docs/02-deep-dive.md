# LSM Tree (Log-Structured Merge Tree) — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
An LSM tree is a disk-based, multi-component index structure that defers and batches mutations. New writes accumulate in a sorted in-memory component (the **memtable**); when it fills, it is flushed verbatim to disk as an immutable, sorted file (an **SSTable**). The structure never updates a record in place — a new value, an update, and a delete are all just newer entries that shadow older ones. A background process called **compaction** periodically merges SSTables (a k-way merge sort) to discard shadowed/deleted data and bound the number of files a read must consult. The canonical model comes from O'Neil, Cheng, Gawlick, and O'Neil, *The Log-Structured Merge-Tree (LSM-Tree)*, Acta Informatica 33, 1996.

### The core building blocks
- **Memtable** — the mutable in-memory buffer, kept sorted by key (usually a skip list or balanced tree). Writes are O(log n) inserts; reads here are the freshest data.
- **WAL / commit log** — an append-only file on disk written *before* the memtable insert is acknowledged. It is the durability mechanism: the memtable is volatile, so the WAL is what survives a crash and is replayed on restart.
- **SSTable (Sorted String Table)** — an immutable on-disk file of key-value pairs sorted by key. Once written it is never modified, only created or deleted. Each SSTable carries auxiliary metadata: a sparse index and a Bloom filter.
- **Sparse index** — keys + file offsets recorded at intervals (e.g. one entry per block), held in or near memory. It narrows a lookup to a single block, which is then scanned linearly.
- **Bloom filter** — a probabilistic membership filter per SSTable. It answers "key is definitely absent" or "key is probably present," letting a read skip SSTables that cannot contain the key.
- **Tombstone** — a special marker entry recording that a key was deleted at a given timestamp. It shadows older live values until compaction physically removes both.
- **Compaction** — the background merge that reclaims space and bounds read fan-out, governed by a compaction *strategy*.

### How it relates to the broader landscape
LSM trees and B+-trees are the two dominant families of on-disk ordered index. The B+-tree (the engine behind InnoDB, PostgreSQL heaps + indexes, and most classic RDBMSs) mutates pages in place and optimizes for read latency and bounded space. The LSM tree is the write-optimized sibling: it converts random writes into sequential ones and pays for it on the read and space side. Hash-based log structures (Bitcask) are a simpler cousin with no range scans. The LSM design is itself a descendant of the log-structured file system idea (Rosenblum & Ousterhout, 1992).

## Where

### Where it runs / lives in the stack
The LSM tree is the **storage engine** — the layer beneath the query/transaction layer and above the block device. It owns the on-disk format, the memory buffers, the WAL, and the compaction scheduler. It does not parse queries or speak a wire protocol; a database embeds it.

### Where you typically encounter it
- **RocksDB** — Facebook's embeddable engine; the de facto reference LSM implementation, forked from Google's LevelDB.
- **LevelDB** — Google's original embeddable LSM library.
- **Apache Cassandra** and **ScyllaDB** — wide-column stores built directly on the LSM/SSTable model.
- **HBase** and **Google Bigtable** — the lineage that popularized SSTables.
- **MyRocks / TiKV / CockroachDB** — SQL or NewSQL systems using RocksDB as the underlying store.
- **InfluxDB, Prometheus (TSM/TSDB variants)** — time-series engines using LSM-like append-and-compact designs.

### Ecosystem and tooling
- **Embeddable engines:** RocksDB, LevelDB, Pebble (CockroachDB's Go rewrite of RocksDB), BadgerDB.
- **Distributed databases built on it:** Cassandra, ScyllaDB, HBase, TiKV.
- **Tuning/observability:** RocksDB's statistics and `LOG`/`OPTIONS` files; Cassandra's `nodetool tablestats`, `nodetool compactionstats`, and per-table compaction strategy settings.
- **Theory/benchmarking:** the RUM conjecture and the Dostoevsky/Monkey line of research on tuning amplification trade-offs.

## When

### When the topic emerged and why
The 1996 paper targeted a concrete pain point: maintaining a real-time index on a high-insert file (the motivating example was transaction history logs). A B-tree turns each insert into a random read-modify-write of a leaf page, roughly doubling I/O cost on insert-heavy workloads. The LSM answer is to batch index changes in memory and cascade them to disk via merge-sort-like passes, trading many small random writes for a few large sequential ones. Google's 2006 Bigtable paper turned the idea into mainstream practice; the open-source wave (LevelDB 2011, RocksDB 2012) followed.

### When to use it in a project
Reach for it when:
- Writes/updates dominate — ingestion pipelines, time-series, metrics, event logs, IoT.
- The disk strongly prefers sequential I/O (this still helps SSDs by reducing write churn and improving compression).
- You need good on-disk compression; sorted immutable blocks compress well.
- You can tolerate background CPU/I/O spent on compaction in exchange for cheap front-door writes.

### When NOT to use it
Avoid it when:
- Point-read latency is the headline metric and the working set does not fit in cache — a B-tree's single seek often beats LSM's multi-SSTable fan-out.
- The workload is read-mostly with rare writes; you pay compaction cost for little write benefit.
- Strict, predictable tail latency is required and you cannot absorb compaction-induced stalls or write stalls.
- The dataset is small enough to fit a simpler in-memory or B-tree index.

## How

### How it works under the hood
**Write path:**
1. Append the mutation to the WAL and fsync (or batch-sync) for durability.
2. Insert into the active memtable (sorted structure). Acknowledge the write to the client.
3. When the memtable hits its size threshold, mark it immutable, start a fresh active memtable, and enqueue the old one for flush.
4. Flush writes the immutable memtable to a new SSTable at the lowest level, building its index blocks and Bloom filter in the same pass. The corresponding WAL segment can now be discarded.

**Read path** (a key may exist in several places; newest wins):
1. Check the active memtable, then any immutable-but-not-yet-flushed memtables.
2. For on-disk SSTables, consult each candidate's Bloom filter first. A "definitely absent" result skips the file with no disk I/O. A "probably present" result proceeds.
3. Use the sparse index to binary-search to the right block, read that block, and locate the key.
4. Return the entry with the highest timestamp. A tombstone counts as "found, but deleted."

Because a read may touch the memtable plus several SSTables, the Bloom filter is what keeps reads from degenerating into one disk seek per file. With ~10 bits per key and the corresponding hash count, the false-positive rate sits near 1%, so only roughly 1 in 100 negative lookups triggers a wasted block read.

**Compaction strategies** (the central design lever):
- **Size-tiered (STCS / RocksDB "universal"):** SSTables of similar size are merged into one larger SSTable. Low write amplification; high space amplification (multiple full copies can coexist during a merge — up to ~2x transiently) and higher read amplification (more overlapping files to check). Cassandra's default is `SizeTieredCompactionStrategy`.
- **Leveled (LCS / RocksDB leveled):** data is organized into levels L0, L1, L2…; within each level beyond L0, key ranges across SSTables are non-overlapping. Each level is a fixed multiple larger than the one above — RocksDB's `max_bytes_for_level_multiplier` defaults to 10. A point read touches at most one SSTable per level, so read and space amplification are low, but compaction rewrites data repeatedly, pushing write amplification above 10x.
- **Tiered+leveled (hybrid):** tiered for the small upper levels, leveled for the large lower levels — a middle ground used by RocksDB's "level" style and others.

### Key trade-offs
| Design choice | Gained | Given up |
|---|---|---|
| Out-of-place writes (append, never overwrite) | Sequential write throughput, simple crash recovery | Stale copies on disk until compaction (space amplification) |
| Multi-level / multi-SSTable layout | Cheap flushes, batched merges | Reads may fan out across files (read amplification) |
| Background compaction | Bounded read fan-out, space reclaim | Compaction burns CPU/I/O; can cause write stalls |
| Leveled vs size-tiered | Leveled: low read/space amp | Leveled: high write amp; STCS inverts the trade |
| Bloom filters | Skip absent-key SSTables cheaply | Extra memory; tunable false positives |

These three knobs — write, read, and space amplification — cannot be minimized simultaneously; the RUM conjecture formalizes that you optimize at most two.

### Common failure modes
- **Write stalls / stop-the-world:** ingestion outpaces flush+compaction, L0 fills, and the engine throttles or blocks writers. Cause: under-provisioned compaction threads or I/O.
- **Read amplification blowup:** too many overlapping SSTables (often under size-tiered) make point reads scan many files. Cause: compaction falling behind or wrong strategy for a read-heavy table.
- **Space amplification spike:** obsolete data and in-flight merge copies bloat disk far beyond logical size. Cause: size-tiered merges of large tiers, or compaction lag.
- **Tombstone accumulation:** masses of deletes/TTL expirations linger and slow reads (and range scans must read past them). Cause: tombstones survive until both they and the shadowed data are compacted together *and* `gc_grace_seconds` (Cassandra default 864000s = 10 days) has elapsed.
- **Bloom filter under-sizing:** too few bits per key raises false positives, turning skips into wasted disk reads.

## Why

### Why it exists
The hardware fact underneath everything: storage devices service large sequential writes far better than small random ones, and historically writing a record was the expensive part of indexing. A B-tree pays a random-I/O tax on every insert to keep the structure sorted and read-optimal at all times. The LSM tree exists to remove that tax: defer ordering work, batch it, and amortize it across many records in the background. It is fundamentally a latency-vs-throughput and now-vs-later bargain on write cost.

### Why it looks the way it does
The obvious alternative — keep a single sorted file and merge each new write in place — is a non-starter because every write would rewrite the file. The next alternative — one in-place-updated B-tree — is exactly what LSM is reacting against. The multi-component design (one mutable memory tier, many immutable disk tiers) falls out of two constraints: writes must be cheap (so buffer and append), and disk files must be safe to share without locking (so make them immutable and reconcile by timestamp). Immutability is the non-obvious linchpin — it makes flushes lock-free, makes SSTables trivially cacheable and replicable, and makes crash recovery a WAL replay rather than a structural repair. The cost it imports — stale data and read fan-out — is precisely what compaction and Bloom filters exist to contain.

### Why it matters now
As of 2026 the LSM tree is the default storage engine for write-heavy and distributed data systems, not a niche choice. RocksDB and its descendants (Pebble, TiKV) sit under a large share of NewSQL and cloud-native databases, and the time-series/observability boom keeps ingestion-first workloads growing. Research continues to push the amplification frontier (learned indexes, learned Bloom filters, adaptive compaction). It is a stable, foundational technology worth understanding precisely because so many systems quietly inherit its trade-offs.

## Open questions / things to verify in practice
- Measure your real write amplification under leveled vs size-tiered compaction on your own data — does it match the ">10x for leveled" rule of thumb?
- Profile read latency with Bloom filters enabled vs disabled to see how much fan-out they actually save for your key distribution.
- Watch what happens at the write-stall threshold: how does the engine behave when compaction falls behind ingestion?
- For delete-heavy or TTL workloads, track tombstone counts and confirm whether `gc_grace_seconds` and compaction are actually reclaiming them.
- Test range-scan latency, not just point reads — range scans can't use Bloom filters and must merge across overlapping SSTables.
- Compare disk footprint (space amplification) over a full compaction cycle against the logical data size.
