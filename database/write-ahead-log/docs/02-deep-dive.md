# Write-Ahead Log — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

A Write-Ahead Log is a **durable, sequentially written, monotonically ordered sequence of physical or physiological change records** that a storage engine emits *before* mutating the corresponding data pages in stable storage. The log obeys two invariants:

1. **WAL rule (log-before-page)** — no dirty data page may be written to its data file until every log record describing a change to that page is durable on disk.
2. **Force-log-at-commit** — a transaction may not be reported committed until its commit log record (and every record it depends on) is durable on disk.

Every record is stamped with a **Log Sequence Number (LSN)**, a strictly increasing integer usually equal to the record's byte offset in the logical log stream. The LSN is the timeline: pages carry the LSN of the last change that touched them (`FIL_PAGE_LSN` in InnoDB, `pd_lsn` in Postgres), so recovery can decide "is this page ahead of, at, or behind this log record?"

### The core building blocks

- **Log record** — a self-describing change unit: header (length, transaction ID, previous-LSN, resource-manager ID, CRC) followed by a body (which page, what bytes, or a logical operation like "insert tuple T into heap page P"). In Postgres the header is `XLogRecord` — 24 bytes containing `xl_tot_len`, `xl_xid`, `xl_prev`, `xl_info`, `xl_rmid`, and `xl_crc` (CRC-32C).
- **LSN** — position in the log stream. Comparisons like `page.lsn < record.lsn` drive the redo decision.
- **Log buffer** — an in-memory ring (Postgres `wal_buffers`, InnoDB `log_buffer`) that batches writes before the sequential `write()`/`fsync()` to disk.
- **Log manager / WAL writer** — the subsystem that owns append, flush, group-commit, and rotation.
- **Log segments / files** — the on-disk chunks. Postgres writes fixed-size **16 MB** files in `pg_wal/` (configurable at `initdb` time via `--wal-segsize`). InnoDB (MySQL 8.0.30+) sizes its redo pool by `innodb_redo_log_capacity` and internally maintains **32 files** of `capacity/32` each. SQLite uses a single sidecar `<db>-wal` file plus a shared-memory index `<db>-shm`.
- **Checkpoint** — a durable marker: "every change with LSN ≤ X is present in the data files." It bounds recovery work.
- **Data page / buffer pool** — the "real" state. Pages live in the buffer pool while hot, and are flushed to the heap/index files lazily, out of order.

The reference formal model is **ARIES** (Mohan et al., 1992, ACM TODS 17(1)) — the algorithm every serious relational engine implements a variant of.

### How it relates to the broader landscape

WAL is one member of the **crash-recovery-via-logging** family. Its siblings are **shadow paging** (Postgres 6.x, LMDB — copy pages instead of logging deltas), **command logging** (VoltDB/H-Store — log the SQL statement, not the physical change), and **log-structured merge trees** (LevelDB, RocksDB, Cassandra — where the WAL is a separate crash safety net and the *data* itself is also log-structured). WAL wins for OLTP because it turns many small random writes into one sequential append, and because the recovery cost is bounded by the checkpoint interval instead of the whole database size.

## Where

### Where it runs / lives in the stack

Inside a single database process, the WAL sits **below the transaction manager and above the storage layer**:

```
        SQL / query executor
                │
        Transaction manager   ── begins, commits, aborts
                │
        Access methods (heap, btree, hash)
                │
   ┌────────────┴────────────┐
   │                         │
Buffer pool              WAL / log manager
   │                         │
Data files (.dat,        WAL segments
  .ibd, tablespaces)      (pg_wal/, ib_logfileN, *-wal)
```

The buffer pool and the log manager are **peers** that gossip through LSNs. Every dirty page in the buffer pool carries its "recovery LSN"; the buffer pool's page cleaner is not allowed to write a page until the log manager confirms the corresponding log records are on disk.

### Where you typically encounter it

- **PostgreSQL** — `pg_wal/` directory, `XLogInsert()`, streaming replication is literally the WAL shipped over TCP.
- **MySQL / InnoDB** — the redo log (physiological, for crash recovery) and undo log (logical, for MVCC and rollback) are separate, both mandatory.
- **SQLite** — opt-in via `PRAGMA journal_mode=WAL;` instead of the default rollback journal (`DELETE`).
- **SQL Server** — `.ldf` transaction log; the entire recovery model (SIMPLE/FULL/BULK_LOGGED) is a policy over the same log.
- **RocksDB / LevelDB / Kafka** — the memtable / commit log / partition log are all WALs in structure, even when the top-level abstraction isn't "relational."
- **etcd, Consul, ZooKeeper** — Raft/Zab log entries are WAL records, replicated instead of just local.

### Ecosystem and tooling

- **Inspection**: `pg_waldump` (Postgres), `mysqlbinlog` / InnoDB's opaque redo (no first-party dumper), `sqlite3` CLI + `.wal` info via `PRAGMA wal_checkpoint`.
- **Replication feeds**: Postgres logical decoding (`pgoutput`, `wal2json`, Debezium), MySQL binlog + row-based replication, Kafka Connect Debezium connectors.
- **PITR / backup**: `pg_basebackup` + WAL archiving, Percona XtraBackup for MySQL, Litestream for SQLite.
- **Standards / specs**: ARIES paper; the Postgres source tree's `src/backend/access/transam/README`; InnoDB internals docs; the SQLite WAL page (sqlite.org/wal.html) is a first-party spec.

## When

### When the topic emerged and why

Pre-WAL relational systems (System R, early Ingres) used **shadow paging** and **force-at-commit**: write every dirty page to disk before commit. This was correct but slow — a commit cost O(pages touched) random writes. IBM's **ARIES** paper (1992) formalized WAL + **STEAL/NO-FORCE** buffer management: pages can be flushed *before* commit (STEAL, needs UNDO) and are *not* required to be flushed at commit (NO-FORCE, needs REDO). This decoupling is what makes commits fast on spinning disks and still cheap on SSDs, because commit becomes a single sequential fsync of a small log tail regardless of how many pages the transaction dirtied.

### When to use it in a project

Reach for a WAL when:

- You are building a stateful store that must survive `kill -9`, OS panic, or power loss without losing acknowledged writes.
- A single logical operation touches multiple pages and must be atomic across them.
- You want commit latency to be independent of transaction size.
- You want replication or CDC — the log is already the change feed.
- You want point-in-time recovery.

### When NOT to use it

Avoid it when:

- Your working set fits in memory and losing it on restart is acceptable (caches, sessions).
- Writes are rare enough that a full-file rewrite + `fsync` + rename is simpler (config files, small key-value stores under a few MB).
- Latency floor matters more than durability — a WAL commit is bounded below by one fsync (~50 µs on NVMe, ~5 ms on a spinning disk).
- The data file is *already* the log (append-only event stores where readers scan from offset zero).

## How

### How it works under the hood

The write path for a single UPDATE:

1. **BEGIN** — the transaction manager assigns an XID.
2. **Modify a page in the buffer pool** — the access method (heap, btree) mutates the in-memory page and marks it dirty. In InnoDB this happens inside a **mini-transaction (MTR)**: a bracketed unit that latches the affected pages, generates redo records into a local buffer, and releases latches on commit of the MTR (not the SQL transaction).
3. **Emit a log record** — the change (page ID, offset, before/after image or logical op, XID, previous-LSN for this XID) is appended to the in-memory log buffer. `XLogInsert()` returns the LSN. The page's `pd_lsn` is updated to this LSN.
4. **First touch after checkpoint** — Postgres additionally writes a **full-page image** into the WAL (see `full_page_writes = on`, default). This defends against **torn pages**: the OS may split an 8 KB page write into two 4 KB sector writes, and a crash between them leaves the on-disk page half-old, half-new. Replaying just the delta on such a page would be undefined; the full image lets recovery overwrite it wholesale. InnoDB solves the same problem via the **doublewrite buffer** in the tablespace, not in the log.
5. **COMMIT** — the transaction writes a COMMIT record and calls the log manager to flush WAL up through that record's LSN. Only after the fsync returns does the client see success. This is force-log-at-commit.
6. **Group commit** — while one backend waits on fsync, other backends' commits pile up in the log buffer. A single fsync durably commits all of them. Postgres exposes `commit_delay` (µs) and `commit_siblings` to intentionally wait for more commits to join; the default `commit_delay=0` disables it. The WAL writer wakes every `wal_writer_delay` (default **200 ms**) to flush asynchronously for `synchronous_commit=off`.
7. **Page flush** — a separate background writer eventually writes the dirty data page to its data file. Because of the WAL rule, it must first confirm the page's `pd_lsn` is `<=` the flushed WAL LSN.
8. **Checkpoint** — periodically, the checkpointer forces every dirty page whose LSN is ≤ some checkpoint LSN, writes a `CHECKPOINT` record, and updates `pg_control` (Postgres) / the redo log header (InnoDB) so the next recovery knows where to start. Postgres triggers on `checkpoint_timeout` (default 5 min) or `max_wal_size` (default 1 GB). SQLite auto-checkpoints when the `-wal` file crosses **1000 pages** (`wal_autocheckpoint`).

Crash recovery replays the log using the **three-phase ARIES algorithm**:

1. **Analysis** — scan forward from the last checkpoint. Rebuild the *transaction table* (which XIDs were live at crash) and the *dirty page table* (which pages might not have made it to disk, and their earliest possible recovery LSN).
2. **Redo** — scan forward from the minimum recovery LSN in the dirty page table. For each record, if `page.lsn < record.lsn`, re-apply the change. This is **repeating history** — even changes made by transactions that will ultimately be rolled back are redone, because the on-disk state must first be brought to exactly what it was at the moment of crash.
3. **Undo** — for every transaction that was live (not committed) at crash, walk its log records backward via the `prev-LSN` chain and reverse each one. Each reversal writes a **Compensation Log Record (CLR)** so that if the system crashes *again* during recovery, the second recovery does not undo the same work twice.

### Key trade-offs

| Choice | Gain | Cost |
|---|---|---|
| Physical logging (before/after byte images) | Idempotent redo, simple recovery | Larger log volume; tightly couples log to on-disk format |
| Logical logging (operation-level) | Compact, portable across page layouts | Redo must be deterministic; complex to make idempotent |
| Physiological (physical page, logical op within page) — what Postgres/InnoDB use | Compact and idempotent | Requires page-level latching semantics during redo |
| STEAL + NO-FORCE (ARIES) | Fast commits, flexible buffer pool | Needs both UNDO and REDO in the log |
| Group commit | Higher commit throughput | Adds latency per individual commit |
| Full-page writes (Postgres) | Torn-page safety with no extra structures | WAL volume spikes right after each checkpoint |
| Doublewrite buffer (InnoDB) | Constant WAL volume | Extra sequential write per page flush |
| Larger checkpoint interval | Less flush pressure | Longer recovery time |

### Common failure modes

- **`fsync` lied.** The disk/controller reported success without hitting stable media; a power loss then loses "committed" transactions. Historically called the "fsyncgate" bug in Postgres 2018.
- **WAL disk fills up.** Archiver is stuck (`archive_command` failing), replication slot is stuck (`pg_replication_slots.restart_lsn` frozen), or checkpoints are too slow. The database refuses new writes.
- **Long-running transaction.** Postgres cannot recycle WAL past the oldest running XID's needs, and cannot vacuum tuples visible only to it. Log volume balloons.
- **Checkpoint storm.** After a burst of writes, the checkpointer tries to flush everything at once, causing a latency spike. Mitigated by `checkpoint_completion_target` (Postgres, default 0.9).
- **Torn page with `full_page_writes=off`.** Recovery restores garbage. Never disable this unless the filesystem (ZFS with 8 KB recordsize, some enterprise arrays) guarantees atomic page-size writes.
- **`synchronous_commit=off` misunderstood.** Trades up to `3 * wal_writer_delay` (~600 ms) of committed data for throughput. Fine for logs, catastrophic for orders.

## Why

### Why it exists

The WAL exists to reconcile three physical facts:

1. Memory is volatile; disks are not.
2. Random disk I/O is orders of magnitude slower than sequential disk I/O — still true on NVMe (roughly 5–10× for small writes).
3. A committed transaction must survive any single failure.

Without a log, ACID durability forces you to fsync every touched data page at commit — an O(pages) random-write cost per transaction. The WAL substitutes O(1) sequential appends and defers the random work to background flushers.

### Why it looks the way it does

The obvious alternative — **shadow paging** — copies each modified page to a new location and atomically flips a pointer at commit. It has no log at all and no recovery pass. It lost because (a) it destroys physical locality (logically adjacent pages end up scattered), (b) it multiplies write amplification when a transaction touches many pages, and (c) it makes MVCC and streaming replication awkward because there is no linear change history to ship. WAL keeps the data file's layout stable, produces a natural replication stream as a free byproduct, and localizes the "hard" durability work into one sequential file.

The **STEAL + NO-FORCE** buffer discipline (requiring both UNDO and REDO) also looks unnecessarily complex versus FORCE (flush at commit, only need UNDO) or NO-STEAL (never flush uncommitted, only need REDO). It won because it removes both the commit-time flush storm *and* the pin-forever memory pressure — the buffer pool becomes a plain LRU cache, independent of transaction boundaries.

### Why it matters now

WAL is the primary durability primitive for effectively every stateful system in a 2026 stack: Postgres 18, MySQL 8.4, SQL Server, SQLite, RocksDB, Kafka, etcd, and every Raft-based control plane inherit its shape. It is also the foundation of the entire CDC/streaming-ETL ecosystem (Debezium, Materialize, ClickHouse's MaterializedPostgreSQL) — they consume the log as a change feed. Understanding it is table stakes for reasoning about replication lag, backup strategies, "why is my disk filling up," and cloud database pricing (managed Postgres bills WAL egress).

## Open questions / things to verify in practice

- Measure the commit-latency cliff on your storage: run a loop of single-row commits with `synchronous_commit=on` vs `off` and observe the tail.
- Force a crash mid-transaction (`kill -9` the server under load) and confirm the recovery log actually replays, including a full-page image case.
- Fill the WAL disk on purpose in a scratch environment. Does the DB stall gracefully or corrupt?
- Turn on `pg_waldump` against a live WAL segment and correlate a specific INSERT to its physical record — does the layout match what the docs claim in your minor version?
- With SQLite WAL, verify what happens when a reader is left open across a checkpoint (`PRAGMA wal_checkpoint(TRUNCATE)`) — does the file actually shrink?
- Compare recovery time after a normal shutdown vs after a `kill -9` at 90% of `max_wal_size`. That delta is your real RTO ceiling.
