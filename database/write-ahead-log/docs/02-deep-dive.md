# Write-Ahead Log — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
A Write-Ahead Log is an append-only, sequentially-written, durably-persisted stream of records describing changes to a database's persistent state, governed by one invariant:

> For every page modification `P` whose effect must survive a crash, the log record describing `P` must reach stable storage *before* the modified page itself does, and before the transaction's commit is acknowledged to the client.

This is the **WAL rule** (sometimes stated as two sub-rules: the *undo rule* — log undo information before flushing a dirty page; the *redo rule* — log redo information before acknowledging commit). It is enforced via **LSN ordering**: every log record carries a monotonically increasing Log Sequence Number, every data page header stores the LSN of the last log record that modified it (`pageLSN`), and the buffer manager refuses to flush a page until the log has been flushed up to that `pageLSN`.

### The core building blocks
- **Log record**: typed payload describing one mutation. Fields commonly include `LSN`, `prevLSN` (previous record of the same transaction, for backward chaining during undo), `transactionID`, `type` (update / commit / abort / CLR / checkpoint), `pageID`, before-image and/or after-image, and a CRC checksum.
- **LSN (Log Sequence Number)**: a 64-bit position into the log. In Postgres it is literally a byte offset into the WAL stream (`pg_lsn`); in InnoDB it is a byte offset into the redo log files.
- **pageLSN**: stamped in every data page header. The buffer manager's flush path enforces `flushedLogLSN >= pageLSN` before the page can be written out.
- **Transaction table & dirty page table**: in-memory structures that ARIES-style recovery rebuilds during the analysis phase to know what to redo and what to undo.
- **Checkpoint record**: a self-describing log record (or pair of records) snapshotting the transaction table and dirty page table, marking a point from which recovery can safely begin scanning.
- **WAL writer / log buffer**: an in-memory ring that batches record formatting before the actual `write()` + `fsync()` to the log file(s).

The canonical formal model is **ARIES** (Mohan et al., IBM, *ACM TODS* 1992) — see the original paper at <https://web.stanford.edu/class/cs345d-01/rl/aries.pdf>.

### How it relates to the broader landscape
WAL is one of three families of crash-recovery techniques. The siblings are **shadow paging** (atomic pointer swap to a new page tree — used in classical System R, LMDB, and ZFS), and **no-steal/force buffer policies** (write all dirty pages at commit, no log needed — historically simple, practically too slow). WAL combines a **steal** policy (dirty pages may be evicted before commit, requiring undo logging) with a **no-force** policy (dirty pages need not be flushed at commit, requiring redo logging). Almost every production OLTP engine in 2026 is a steal/no-force, ARIES-descended WAL system.

## Where

### Where it runs / lives in the stack
At the storage-engine layer, sitting between the buffer manager and the operating system's file API. The log itself lives on a local block device (ideally a dedicated, fsync-fast one — NVMe or battery-backed SSD). The log is **not** an application concern; clients of the database never touch it directly, but operational tooling (replication, PITR, CDC) reads it as a first-class stream.

### Where you typically encounter it
- **PostgreSQL** — `pg_wal/` directory, 16 MB segments by default, physio-logical records.
- **MySQL / InnoDB** — `ib_logfile0`, `ib_logfile1` (pre-8.0.30) or `#innodb_redo/` (8.0.30+), purely physical redo, plus a separate logical **binlog** at the server layer.
- **SQLite** — `-wal` and `-shm` sidecar files when `PRAGMA journal_mode=WAL` is set. The default since 3.7 (2010) for most embedded use.
- **RocksDB** and other LSM-tree systems (Cassandra commitlog, LevelDB, ScyllaDB) — WAL backs the in-memory memtable; once a memtable flushes to an SSTable, its WAL segment can be discarded.
- **Etcd / BoltDB / FoundationDB** — Raft log and storage-engine WAL.
- **Kafka** is *log-shaped* but is not a WAL in the database-engine sense; it is the durable log itself as the product.

### Ecosystem and tooling
- For inspecting WAL: `pg_waldump` (Postgres), `mysqlbinlog` and the InnoDB `ib_logfile` parsers (MySQL), `ldb` (RocksDB).
- For shipping WAL: `pg_receivewal`, `wal-g`, `pgBackRest`, Barman, MySQL's `mysqlbinlog --read-from-remote-server`.
- For CDC on top of WAL: Debezium (uses Postgres logical decoding via `pgoutput`, MySQL binlog row events, MongoDB oplog).
- Output plugins for Postgres logical decoding: `pgoutput` (built-in, binary), `wal2json` (JSON for external consumers — see <https://github.com/eulerto/wal2json>), `test_decoding` (human-readable, debugging only).

## When

### When the topic emerged and why
ARIES was published in 1992 to solve a tangle of problems in System R-era recovery: how to support fine-grained record locking, partial rollbacks (savepoints), and efficient recovery after a crash, all while letting the buffer manager evict dirty pages whenever it wanted. Before WAL, engines either forced all dirty pages on commit (slow) or used shadow paging (no in-place updates, fragmentation, painful with B-trees). The combination of sequential log writes + lazy random page writes is essentially a buffering trick: convert N random writes into one sequential write now, plus N batched random writes later.

### When to use it in a project
Reach for it when:
- You are building any system that owns durable state and must survive a crash mid-write without losing acknowledged commits.
- You want commit latency dominated by one fsync per group of transactions, not by random I/O.
- You need point-in-time recovery, streaming replicas, or change-data-capture downstream.
- You need atomic multi-page updates (transactions that touch more than one B-tree page).

### When NOT to use it
Avoid it when:
- State is genuinely ephemeral (caches, session stores you can rebuild).
- You can express durability as "the source upstream is the truth, I am just a derived view" (a materialized read replica fed by another WAL).
- You are append-only and idempotent against an external source (some ETL/ELT pipelines).
- You are layering above a database that already has a WAL — do not write your own on top.

## How

### How it works under the hood
A typical commit path in a WAL-backed engine:

1. **Mutate in memory.** The transaction modifies a buffer-pool page in place. The page is now *dirty*, and its `pageLSN` is updated to the LSN of the new log record.
2. **Append a log record.** A redo (and, for steal engines, undo) record is formatted into the in-memory WAL buffer. Returns the assigned LSN.
3. **Flush log up to commit LSN.** On `COMMIT`, the WAL writer flushes the buffer to the log file via `write()` and then `fsync()` (or `fdatasync` / `O_DSYNC`, depending on `wal_sync_method`). Only after the syscall returns is the commit acknowledged.
4. **Acknowledge the client.** The transaction is now durable. The dirty data page is still in the buffer pool and may not hit disk for seconds or minutes.
5. **Background writer / checkpointer** eventually writes dirty pages out, respecting the WAL rule (`flushedLogLSN >= pageLSN`).
6. **Checkpoint.** Periodically the engine writes a checkpoint record, ensuring all dirty pages older than some LSN `C` have been flushed. WAL segments older than `C` can now be recycled (subject to replication-slot and archive retention).
7. **Crash + recovery.** On restart the engine reads the last checkpoint record, then runs ARIES-style three passes:
   - **Analysis** — forward scan from the checkpoint; rebuild the transaction table and dirty page table; determine the *redo LSN* (oldest unflushed page's recLSN).
   - **Redo** — forward scan from the redo LSN; for each log record, if `pageLSN < recordLSN` then reapply the change (idempotent because of the LSN check). ARIES "repeats history" — it redoes the work of *all* transactions, including those that will later be rolled back.
   - **Undo** — backward scan following `prevLSN` chains for transactions that were active at crash time; emit Compensation Log Records (CLRs) so that a crash *during recovery* is itself recoverable.

A note on logging granularity, since this is where engines diverge:

- **Physical logging**: byte-level before/after images of pages. Cheap to apply, but tied to exact page layout — so a replica must be byte-identical. InnoDB redo is mostly physical.
- **Logical logging**: row-level operations ("insert these column values into table T"). Portable across versions and even storage engines, but expensive to apply (must walk indexes, fire triggers in some implementations). MySQL's **binlog** is logical (or row-based, which is still semantically logical at row granularity).
- **Physiological logging** (Gray's term): physical-to-a-page, logical-within-the-page. The record names a specific page but describes the change in terms the page format understands ("delete tuple slot 7"). Postgres's WAL is physiological, and it falls back to **full-page writes** for the first modification of each page after a checkpoint — necessary because the OS may tear an 8 KB write into two 4 KB sector writes mid-power-loss, and a torn page cannot be patched by a logical delta. See <https://wiki.postgresql.org/wiki/Full_page_writes>.

InnoDB's split is the textbook case: the redo log is the physical WAL (engine-internal, crash recovery), and the binlog is the logical change stream (replication, PITR, audit). MySQL uses a two-phase commit between the two so they cannot disagree after a crash.

### Key trade-offs

| Design choice | Gained | Given up |
| --- | --- | --- |
| Sequential log + lazy page flush | Commit dominated by one fsync, not N random writes | Recovery is non-trivial; readers may need to consult both pages and log |
| Physical / physiological logging | Cheap, deterministic redo; small log records | Replica must match page format; cross-version replication is hard |
| Logical logging (binlog) | Portable, human-readable, replicable across engines | Larger, slower to apply; can drift from physical state |
| Full-page writes after checkpoint | Survives torn pages | WAL volume balloons right after each checkpoint |
| Group commit | Amortizes fsync cost across many transactions (10x–100x throughput under load) | Adds tail latency on the order of a few milliseconds |
| `synchronous_commit = off` (Postgres) | Higher throughput, lower commit latency | Up to `wal_writer_delay` (200 ms default) of acknowledged commits can be lost on crash — database stays consistent, but recent commits disappear |
| Large checkpoint interval | Less write amplification | Longer recovery time after a crash |

### Common failure modes
- **fsync lying.** Disk firmware or virtualized storage acknowledges fsync before the data is on stable media — a single power-loss event then loses "committed" data. Battery-backed write caches or `fsync_writethrough` mitigate this.
- **fsyncgate (2018).** Linux's page cache silently drops dirty pages after a writeback error, and a *second* fsync returns success. Postgres now `PANIC`s on fsync failure (back-patched to 9.4+; see <https://wiki.postgresql.org/wiki/Fsync_Errors>).
- **Torn pages.** Power loss mid-page-write leaves a half-old, half-new page; redo of a physiological record against it produces garbage. Mitigated by full-page writes (Postgres), the double-write buffer (InnoDB), or atomic-write hardware.
- **Log on the same spindle as data.** Sequential log writes compete with random data writes; both throughput and latency collapse. Put WAL on a separate device.
- **Replication slot retains WAL forever.** A logical replication consumer that stops consuming pins WAL on the primary; `pg_wal/` fills the disk; the database refuses writes. Always monitor slot lag.
- **Checkpoint storms.** A too-aggressive checkpoint flushes many pages at once, saturating I/O and stalling commits.
- **WAL volume amplification.** A workload that updates many distinct pages right after a checkpoint generates huge full-page-write traffic; tuning `checkpoint_timeout` and `max_wal_size` is non-trivial.

## Why

### Why it exists
WAL exists because two physical facts are non-negotiable. First, random I/O is roughly two orders of magnitude slower than sequential I/O, even on NVMe. Second, durability is binary: a committed transaction must survive every fault short of media loss. WAL converts an unsolvable "fast and durable random writes" problem into a tractable "fast durable sequential writes + lazy random writes" problem. Everything else — recovery algorithms, replication, CDC — is downstream of that conversion.

### Why it looks the way it does
The non-obvious choice is *redoing committed and uncommitted work alike during recovery before undoing losers*. The naïve alternative is "during redo, only reapply committed transactions." ARIES rejects that because of fine-grained locking: a record-level lock can be held by an uncommitted transaction while a *committed* transaction has already modified an adjacent record on the same page; reapplying only one of them produces an inconsistent page. "Repeat history, then undo" is what makes record-level locking compatible with page-level redo. Same reason CLRs exist: they make undo idempotent and forward-progressing, so a crash during recovery still terminates.

The other non-obvious choice is **steal + no-force**. The simpler alternatives are (a) force-at-commit (no redo log needed, but commit becomes O(dirty pages)) and (b) no-steal (no undo log needed, but buffer pool can't evict). Both fail under realistic OLTP load. Carrying both redo and undo information is the price for letting the buffer manager and the commit path be independent.

### Why it matters now
WAL is the layer where 2026's interesting database work is happening. Logical decoding has turned every Postgres into a streaming source for analytics and event-driven systems (Debezium, Materialize, Sequin). Disaggregated-storage databases (Aurora, Neon, AlloyDB) push the WAL itself to a distributed storage service and rebuild pages from the log — effectively making the log *be* the database. NVMe and persistent memory have shifted the bottleneck from fsync latency to log-record formatting CPU cost, which is why every major engine has rewritten its WAL writer in the last five years. None of this is going away.

## Open questions / things to verify in practice
- On your specific storage (cloud block device, local NVMe, RAID), how long does one fsync of a 4 KB write actually take? Measure with `pg_test_fsync` or `fio`.
- What is the throughput cliff at which group commit stops helping and you become CPU-bound on log record formatting?
- For your workload, what fraction of WAL bytes is full-page writes? (Postgres: `pg_stat_wal.wal_fpi` vs `wal_records`.) If high, lengthen `checkpoint_timeout`.
- What is your real recovery time after a crash with a representative dirty buffer pool? Time it; do not assume.
- If you enable a logical replication slot and the consumer dies, how long until your primary's WAL volume becomes a problem? Set `max_slot_wal_keep_size`.
- Does your storage actually honor fsync? Pull the plug on a test machine mid-load and check.
