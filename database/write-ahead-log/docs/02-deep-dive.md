# Write-Ahead Log — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

A Write-Ahead Log is an append-only, sequentially written file (or ring of files) of change records that a storage engine uses to satisfy the **WAL invariant**:

> Every log record describing a change to page P must be durable on stable storage **before** the modified image of P is written back to the data files, **and** all log records for a transaction T must be durable before T is reported committed.

The first half (log-before-page) is what makes crash recovery possible without double-writing the data. The second half (log-before-ack) is what makes durability (the D in ACID) a real promise instead of a hope.

### The core building blocks

- **Log record** — a fixed header plus a payload describing one change. In PostgreSQL the header includes `xl_tot_len`, `xl_xid` (transaction id), `xl_prev` (byte offset of the previous record — the prev-LSN chain), `xl_info` (rmgr flags), `xl_rmid` (resource manager, e.g. Heap, Btree, XLOG), and `xl_crc`. In ARIES-style engines the header additionally carries a `PageID`, `PrevLSN` for this transaction (so undo can walk backwards), and a type tag (update / compensation / commit / checkpoint).
- **LSN (Log Sequence Number)** — a monotonically increasing 64-bit identifier. In PostgreSQL an LSN literally *is* a byte offset into the logical log stream, so `lsn_b - lsn_a` gives the bytes of WAL between them. Every data page carries a `pageLSN` header pointing at the last record that modified it — recovery uses this to decide "skip or redo".
- **prev-LSN chain** — each record for a given transaction points at that transaction's previous record. Undo walks this backward chain instead of scanning the whole log.
- **WAL buffer** — an in-memory ring that in-flight records are appended to before they hit the OS.
- **WAL segment file** — the on-disk unit. Postgres uses 16 MB segments by default (tunable via `--wal-segsize` at `initdb`); InnoDB uses circular `#innodb_redo/*` files sized by `innodb_redo_log_capacity` (default 100 MB in MySQL 8.0.30+).
- **Flush / fsync** — the OS call that pushes writes past the kernel page cache to persistent media. WAL correctness hinges on this actually happening; a lying `fsync` is why "power-loss protection" matters for enterprise SSDs.
- **Checkpoint** — the operation that (1) writes all dirty buffer-pool pages older than a chosen LSN to the data files, (2) records a checkpoint marker in the log, and (3) updates a control file (`pg_control` in Postgres) so recovery knows where to start replay. WAL older than the checkpoint LSN becomes reclaimable.
- **Logging discipline** — **physical** (byte images of pages), **logical** (semantic operations like "insert row R into table T"), or **physiological** (logical *within* a page, physical *between* pages). ARIES uses physiological. Postgres is mostly physical with full-page images; InnoDB's redo log is physical, its undo log is logical.

### How it relates to the broader landscape

WAL is one specific implementation of the broader idea of **journalling for crash-consistent updates**. Its siblings are **shadow paging** (SQLite's default rollback-journal mode is a variant — write new pages, then atomically swap), **copy-on-write B-trees** (LMDB, ZFS, Btrfs — the tree root pointer is the commit), and **log-structured storage** (LSM trees like RocksDB, or fully log-structured filesystems like F2FS — the log *is* the data). WAL wins where you want fast in-place updates *and* durability without paying random-write latency per commit.

## Where

### Where it runs / lives in the stack

WAL lives in the **storage engine layer** of a database, below the query planner and above the OS file system. It is a private durability primitive: applications never talk to it directly, they just see the durability guarantee that flows out of it. In distributed systems that need consensus (etcd, TiKV, CockroachDB) the WAL sits under the Raft log, and Raft appends *are* WAL appends.

### Where you typically encounter it

- **PostgreSQL** — `pg_wal/` directory of 16 MB segments; `pg_control` tracks the redo LSN.
- **MySQL / InnoDB** — a redo log (physical, in `#innodb_redo/`) plus an undo log (logical, kept in undo tablespaces), plus a separate `binlog` that lives one layer up and drives replication.
- **SQLite in WAL mode** (`PRAGMA journal_mode=WAL`) — writes go to `<db>-wal`, readers consult a `<db>-shm` shared-memory index. The default remains rollback journal for backwards compatibility.
- **RocksDB / LevelDB** — every `Put` first goes to a WAL file, then to the in-memory memtable. Flushing the memtable to an SSTable is the effective checkpoint; the WAL segment then gets purged.
- **etcd** — Raft entries are persisted through a WAL before the leader considers them committed; recovery replays the WAL and then applies committed entries to the key-value store.
- **Filesystems** — ext4's `jbd2` journal, XFS's transaction log, NTFS `$LogFile`. Same pattern: intent recorded first, metadata (or data, in `data=journal` mode) updated after.
- **Kafka** — the pattern is inverted: the log *is* the primary data structure, and consumers read directly from segment files. There is no separate "data file to flush to." This is why Kafka is best thought of as WAL-as-a-product.

### Ecosystem and tooling

- **Inspection / debugging** — `pg_waldump` (Postgres), `mysqlbinlog` (MySQL binlog; the redo log itself is not user-inspectable), `ldb dump_wal` (RocksDB), `sqlite3 .wal` inspection via `PRAGMA wal_checkpoint`.
- **Replication / CDC** — Postgres logical decoding (`pgoutput`, `wal2json`, Debezium), MySQL binlog readers (Debezium, Maxwell), Kafka Connect. All of these are downstream consumers of a WAL.
- **Backup / PITR** — `pg_basebackup` + WAL archiving (`archive_command`, or tools like `pgBackRest`, `WAL-G`, `Barman`). InnoDB uses Percona XtraBackup / `mysqlbackup`.
- **Consensus atop WAL** — Raft implementations (etcd, hashicorp/raft, tikv/raft-rs) always have a WAL under the hood.

## When

### When the topic emerged and why

The formal treatment is Mohan et al., *"ARIES: A Transaction Recovery Method Supporting Fine-Granularity Locking and Partial Rollbacks Using Write-Ahead Logging"* (IBM, ACM TODS 1992). Before ARIES, systems either used **shadow paging** (System R) — which forced a full page copy per update and interacted badly with fine-grained locking — or **force-at-commit** (write every modified page at commit time), which turned every commit into a random-I/O storm. ARIES gave you no-force / steal buffer management (dirty pages can be flushed early, clean pages need not be flushed at commit) with a rigorous recovery algorithm on top. Every major relational engine that shipped after ~1995 is a direct descendant.

### When to use it in a project

Reach for a WAL when:

- You are building a storage layer that must survive `kill -9` or power loss without losing acknowledged writes.
- You do in-place page updates (B-tree, heap file) and want cheap atomic multi-page transactions.
- You need a linearizable stream of state changes — for replication, CDC, or Raft.
- Your workload is write-heavy on media where random I/O is expensive (spinning disks, network-attached storage, cheap SSDs without power-loss protection).

### When NOT to use it

Avoid it when:

- The data structure is already a log (Kafka topic, event store, append-only file). Adding another WAL is pure overhead.
- You have no durability requirement (an in-process cache, a scratch buffer).
- The whole dataset is smaller than one page and rewriting it is faster than log-and-checkpoint bookkeeping (tiny embedded configs).
- You need transactional guarantees across independent systems — that is what two-phase commit or Sagas are for; a WAL only covers a single storage engine.

## How

### How it works under the hood

The commit path in a typical in-place engine (Postgres-shaped, but the shape generalizes):

1. **Modify in the buffer pool.** A backend pins page P, applies the change in memory, bumps `page.pageLSN`, and marks the buffer dirty.
2. **Format a WAL record.** The change is serialized into a record with `{LSN, prev-LSN, xid, rmid, payload}`. If this is the first modification of P since the last checkpoint, a **full-page image** is included (protects against torn writes — a partial 8 KB page write after a power loss).
3. **Append to WAL buffer.** The record is copied into the shared WAL buffer under a lightweight lock; the LSN is stamped from the current insertion pointer.
4. **On COMMIT.** The backend writes a `COMMIT` record, then blocks on `XLogFlush(commitLSN)`. That call issues a `write()` up to `commitLSN` and an `fsync()` (or `fdatasync()`, or `O_DIRECT` write, depending on `wal_sync_method`). Multiple committing transactions coalesce into a **group commit** — one `fsync` retires the whole batch. Postgres exposes this via `commit_delay` / `commit_siblings`.
5. **Ack the client.** Only now. Under `synchronous_commit = on` this is the default. `synchronous_commit = off` returns before flush, capping data-loss risk at `3 * wal_writer_delay` (default `wal_writer_delay = 200 ms`, so up to ~600 ms of committed transactions can vanish on a crash — durability traded for latency).
6. **Later, in the background.** The bgwriter / page cleaner asynchronously writes dirty pages back to the data files. The WAL invariant is preserved because a dirty page P is never written to disk before its highest `pageLSN` has been flushed in the WAL.
7. **Checkpoint.** Periodically (Postgres: `checkpoint_timeout` default 5 min, or `max_wal_size` default 1 GB, whichever hits first), all pages dirtied before the checkpoint LSN are flushed, a `CHECKPOINT` record is written and fsynced, and `pg_control` is updated. WAL segments older than the checkpoint's redo LSN become eligible for recycling or archiving.

Crash recovery follows the ARIES three-pass structure:

- **Analysis** — scan forward from the last checkpoint record. Rebuild the Transaction Table (which transactions were live) and the Dirty Page Table (which pages had unflushed changes, tagged with `RecLSN`, the earliest LSN that dirtied them).
- **Redo** — scan forward from `min(RecLSN)`. For each record whose target page has `pageLSN < recordLSN`, re-apply it. This is **idempotent**: replaying a redo whose effect is already on disk is a no-op because the LSN comparison filters it. That's the property that makes recovery deterministic and restartable.
- **Undo** — for every transaction still in the Transaction Table (never committed), walk its prev-LSN chain backward, applying the inverse of each change and writing a **Compensation Log Record** (CLR) for each undo. CLRs point past the record they compensate (their `undoNext`) so a crash *during* undo still resumes correctly.

Two variants worth naming:

- **Physiological logging (ARIES)** — the record says "on page 42, at slot 3, replace bytes 8..40 with X". The `page 42` part is physical (which page). The `slot 3` part is logical (survives page reorganizations within the page). This is what allows in-page compaction without invalidating the log.
- **Logical logging (InnoDB undo, MySQL binlog)** — the record says "INSERT (1, 'foo') into table T". Smaller, replayable across schema-compatible instances, but harder to apply deterministically after a crash mid-statement.

### Key trade-offs

| Design choice | Gained | Given up |
|---|---|---|
| Sequential append vs. in-place writes | Turns random 4–16 KB commit I/O into a stream — modern NVMe hits ~7 GB/s sequential vs. ~1 ms per random `fsync`; a 1 KB WAL record at ~50 μs of `fsync` on Optane is orders of magnitude cheaper than a random page flush. | An extra write per change — data hits disk twice (log + data files). This is the "write amplification of durability." |
| Physical / full-page images | Bulletproof recovery — torn pages are healed by re-applying the full image. | WAL volume balloons (a full-page image costs 8 KB per touched page per checkpoint cycle). Postgres `wal_compression` mitigates. |
| Physiological logging | Small records + page-format flexibility (compaction, slot reordering). | Cannot be applied out-of-order; replay must respect LSN ordering strictly. |
| Group commit | Amortizes one `fsync` across many transactions — throughput goes from ~1 / `fsync_latency` to hundreds of commits per flush. | Individual commit latency rises by up to `commit_delay`. Bad for latency-sensitive OLTP if tuned aggressively. |
| No-force / steal buffer mgmt | Buffer pool can flush dirty pages any time, need not flush at commit. Removes I/O from the commit path. | Requires the full undo machinery for crash recovery — you may crash with uncommitted changes already on disk. |
| Replication piggybacking on WAL | Free — the stream already exists. Byte-perfect physical replicas. | Physical replicas must be exact binary copies (same version, same architecture, same page layout). MySQL's separate binlog trades this rigidity for cross-version flexibility. |

### Common failure modes

- **Lying `fsync`** — consumer SSDs without power-loss protection ack `fsync` before the write reaches flash. Correctness is voided; use enterprise drives or file-system barriers.
- **Torn page** — power loss between the first and last sector of an 8 KB write leaves a half-updated page. Mitigation: full-page images in WAL (Postgres `full_page_writes = on`, default) or InnoDB's doublewrite buffer.
- **WAL disk fills up** — because a long-running transaction or a lagging replica pins segments. Postgres refuses new writes; production incidents follow. Symptoms: `pg_wal/` grows without bound. Tune `max_slot_wal_keep_size`.
- **Checkpoint stall** — a `checkpoint_timeout` fires and the bgwriter cannot keep up, so a huge write spike hits the data disk. Symptoms: latency spikes every 5 minutes. Tune `checkpoint_completion_target` (Postgres default 0.9) or shorten intervals.
- **Recovery time regression** — infrequent checkpoints let the log-to-replay grow into hours of REDO after a crash. RTO trade-off is real.
- **`synchronous_commit = off` on a system that promised durability** — silent data loss window of hundreds of ms per crash. Legitimate for logs/metrics; catastrophic for payments.

## Why

### Why it exists

Every durable database has the same core problem: a commit implies persistence, but persistence to random locations across many pages is slow, and doing it atomically across those pages is even harder. WAL decouples the two: **commit means "the intent to change is durable"**, not "the change is in place." The actual data movement can be batched, reordered, and coalesced. First principles at play: sequential I/O is 10–1000× cheaper than random I/O on every storage medium invented so far (HDD, SATA SSD, NVMe, network block storage), and turning `N` random writes into `1` sequential write plus `1` `fsync` is the cheapest possible way to buy the D in ACID.

### Why it looks the way it does

The obvious alternative — **shadow paging** — updates copies of pages, then atomically swaps a root pointer. It sounds cleaner (no separate log, no replay). But it interacts badly with concurrency: fine-grained row locks assume in-place updates, and shadow paging forces a full-page copy for a single-row change. It also fragments the on-disk layout because logical page order and physical page order diverge. ARIES chose in-place updates + WAL specifically to preserve clustering, support fine-grained locking, and keep dirty pages out of the commit path (no-force). The complexity of undo/redo/CLRs is the price paid; the payoff is that a busy OLTP system can commit thousands of small transactions per second on a single spinning disk — a workload shadow paging cannot touch.

### Why it matters now

The pattern has quietly become the substrate of modern distributed systems. Every Raft-backed system (etcd, Consul, TiKV, CockroachDB, MongoDB) uses a WAL under its consensus layer. Every cloud-native OLTP database (Aurora, AlloyDB, Neon, PlanetScale) is architected around **disaggregating the WAL from the page store** — the log becomes the network-shipped source of truth, and page servers rebuild state from it. Change Data Capture, event-driven architectures, and streaming analytics all depend on the WAL being replayable. In 2026, if you understand WAL, you understand the shape of about 80 % of the persistence layer of the systems you actually run.

## Open questions / things to verify in practice

- Does your storage actually honour `fsync`? Run `diskchecker.pl` or `pg_test_fsync` and confirm the numbers match the drive spec.
- What is your `checkpoint_timeout` vs. `max_wal_size` interaction under peak write load — which one fires first, and does the resulting I/O spike hurt latency?
- How much WAL does one hour of typical workload generate? This sets your archive bandwidth, replication lag ceiling, and RPO.
- What happens if you kill the primary during a `synchronous_commit = remote_apply` transaction? Does the standby fence itself, or accept the write?
- On InnoDB, do you actually need the binlog if you're already streaming redo via group replication? Turning it off is a real throughput win — verify with `sysbench`.
- With `synchronous_commit = off`, exactly how many committed transactions can you lose on a hard crash? Measure it, don't assume the docs.
