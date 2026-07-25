# Write-Ahead Log — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

You don't "use" a WAL — you inherit one. Every mainstream OLTP database sits on top of one, and your job as an operator is to tune it, monitor it, and keep it from filling the disk. The moment your service crosses from "toy" to "production" you become a WAL operator, whether you meant to or not.

In a typical SaaS backend, the WAL is the reason your Postgres instance keeps its ACID promise on an EC2 spot interruption, the reason your standby replica exists at all (streaming replication *is* the WAL over TCP), and the reason your on-call gets paged at 3 a.m. when `pg_wal/` hits 95%. In an analytics pipeline, the WAL is the source of truth for Debezium/Kafka CDC — the log is the change feed. In a mobile app shipping SQLite, `journal_mode=WAL` is the single knob that turns "one writer blocks all readers" into "concurrent readers with an active writer."

Beyond databases, the WAL pattern leaks everywhere: Kafka partitions are WALs, Raft/etcd logs are replicated WALs, event-sourced services persist a WAL as their primary data model. Recognizing the shape is what lets you reason about replication lag, backup RPO, and disk-fill incidents across all of them with one mental model.

## Best practices

### 1. Leave `fsync` and `synchronous_commit` at their safe defaults on primaries
**Do:** Keep `fsync=on` and `synchronous_commit=on` (Postgres), or `innodb_flush_log_at_trx_commit=1` (MySQL), on any database holding money, orders, or user-visible state.
**Why:** These are the settings that translate "COMMIT returned 200 OK" into "the row survives a power cut." Downgrading them silently converts durability into a suggestion.
**Avoid:** Disabling fsync for a benchmark and forgetting to turn it back on — this has caused public post-mortems.

### 2. Use `synchronous_commit=off` (or InnoDB `=2`) *deliberately*, not by default
**Do:** Downgrade durability only per-transaction (Postgres lets you `SET LOCAL synchronous_commit = off`) for high-volume, replayable writes: audit trails, click events, metric ingestion.
**Why:** You buy 5–10× commit throughput at the cost of up to ~600 ms of *committed* data on crash. Fine for logs; catastrophic for `orders`.
**Avoid:** Global `synchronous_commit=off` on a mixed-workload primary — it lies to every caller uniformly.

### 3. Size `max_wal_size` and `checkpoint_timeout` for your write burst, not the default
**Do:** On any busy Postgres, raise `max_wal_size` (default 1 GB) to 4–16 GB and `checkpoint_timeout` (default 5 min) to 15–30 min. Watch `pg_stat_bgwriter.checkpoints_timed` vs `checkpoints_req` — you want almost all timed.
**Why:** Frequent checkpoints cause I/O storms and force redundant full-page images into the WAL right after each one, doubling WAL volume during writes.
**Avoid:** Leaving defaults on a 10k TPS instance and blaming the disk when p99 spikes every 5 minutes.

### 4. Cap replication slots with `max_slot_wal_keep_size`
**Do:** On Postgres 13+, always set `max_slot_wal_keep_size` (e.g. `100GB`). Alert on `pg_replication_slots.wal_status = 'lost'` and on any slot inactive for >5 minutes.
**Why:** An inactive slot is a promise the primary will keep WAL forever for a consumer that no longer exists. This is the #1 cause of "Postgres filled the disk and went read-only" incidents.
**Avoid:** Creating a logical slot for a Debezium prototype, deleting the container, and leaving the slot behind. It will silently eat your disk.

### 5. Archive WAL for PITR — and monitor the archiver
**Do:** For any database whose loss you'd have to explain in a post-mortem, set `archive_mode=on` with an `archive_command` that ships each segment to durable object storage (S3, GCS). Combine with `pg_basebackup` for base snapshots. Test restores quarterly.
**Why:** Base backup + archived WAL is the only way to get to arbitrary RPO (down to one transaction). Streaming replicas protect against hardware failure, not `DROP TABLE`.
**Avoid:** Trusting an untested backup. `archive_command` failing silently is a common cause of an unbounded `pg_wal/`.

### 6. Turn on WAL compression when CPU is cheaper than IOPS
**Do:** Set `wal_compression=on` (Postgres) or `binlog_transaction_compression=ON` (MySQL 8.0+) on write-heavy instances where full-page images dominate volume.
**Why:** Cuts WAL volume 40–70% for typical OLTP with `full_page_writes=on`. Directly reduces replication bandwidth, archive storage cost, and disk pressure between checkpoints.
**Avoid:** Turning it on blindly on CPU-bound instances — check `pg_stat_wal.wal_bytes` and CPU headroom first.

### 7. Never disable `full_page_writes` unless your storage guarantees atomicity
**Do:** Leave `full_page_writes=on` in Postgres, and keep InnoDB's doublewrite buffer enabled, on any block storage. Only disable if you're on ZFS with matching recordsize, or a device with power-loss-protected atomic writes.
**Why:** Torn pages during a power failure produce silent corruption that recovery cannot detect. You will find out weeks later, during a completely unrelated query.
**Avoid:** Copy-pasting `full_page_writes=off` from a benchmark blog. That blog was measuring throughput on ramdisk.

### 8. Watch replication lag in bytes, not seconds
**Do:** Alert on `pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn)` (bytes behind), not just `replay_lag` (time). Track it per replica and per logical slot.
**Why:** Time-based lag can look small on an idle system while byte-lag is exploding, and vice versa. Bytes tell you disk pressure and CDC catch-up cost; time tells you read staleness.
**Avoid:** A single "lag_seconds < 60" alarm — it will not fire until it is already too late to fix.

### 9. Separate WAL and data on different disks when you can
**Do:** On self-managed instances with real burst writes, put `pg_wal/` on its own volume with independent IOPS. On cloud (RDS, Aurora, Cloud SQL) this is done for you.
**Why:** WAL is sequential and latency-sensitive; data files are random and throughput-sensitive. Sharing one volume means a checkpoint's random flushes stall your commit fsyncs.
**Avoid:** Doing this on a laptop or a 50 QPS side project — the operational cost isn't worth it below real load.

### 10. Prefer logical replication for cross-version, cross-schema; physical for HA
**Do:** Use physical streaming replication (Postgres) or row-based binlog replication (MySQL) for hot-standby failover. Use logical decoding / GTID binlog for CDC into Kafka, cross-major-version migrations, and partial-schema replication.
**Why:** Physical is byte-for-byte identical and fast to fail over, but locks you to identical versions and full-cluster copies. Logical is flexible but higher CPU on the primary and constrained (no DDL, no sequences by default).
**Avoid:** Building a bespoke trigger-based CDC when logical decoding exists in your engine. You'll rebuild half of Debezium, badly.

### 11. In SQLite, `journal_mode=WAL` is almost always the right default for apps
**Do:** `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;` for any SQLite embedded in an app that has more than one thread or process reading.
**Why:** Rollback journal serializes readers behind writers. WAL mode lets readers proceed against a snapshot while a writer commits — often 2–10× throughput for mixed workloads.
**Avoid:** WAL mode on a network filesystem (NFS, SMB). SQLite's WAL depends on shared memory (`-shm`) that these filesystems don't support correctly. Data corruption follows.

## Anti-patterns to recognize

- **The "fsync-off benchmark" that ships**: someone disables fsync to win a Twitter benchmark, ships it, and discovers the definition of "durable" the hard way after a rack reboot. The fix is a code review rule: `fsync=off` never leaves a load-test config.
- **The orphaned replication slot**: a CDC consumer or dev replica is torn down, the slot stays, WAL grows for days until the disk fills and the primary goes read-only. Fix: `max_slot_wal_keep_size` + alerts on inactive slots older than N minutes.
- **The stuck `archive_command`**: `aws s3 cp` starts failing (credentials, quota, network), Postgres holds every segment waiting to retry, disk fills. Fix: monitor `pg_stat_archiver.last_failed_wal` and page on any nonzero gap.
- **The unbounded long-running transaction**: an analytics query or forgotten `BEGIN` on a psql console holds the oldest XID open. WAL cannot be recycled, vacuum cannot clean, table bloats. Fix: `statement_timeout` and `idle_in_transaction_session_timeout` set globally.
- **Confusing binlog with redo (MySQL)**: engineers assume "the binlog is my crash log." It isn't — the InnoDB redo log is. The binlog is for replication and PITR. Disabling either for "performance" breaks a different thing.
- **PITR without restore drills**: WAL archiving is on, base backups exist, nobody has ever restored. The first restore during an incident finds a bug in the archive path from six months ago. Fix: automated monthly restore into a scratch environment with checksum verification.
- **Treating `checkpoint_completion_target` as a tuning dial**: sweeping it toward 1.0 to "spread out" I/O while also shrinking `checkpoint_timeout` and getting more, longer, overlapping checkpoints. Fix: increase `max_wal_size` and `checkpoint_timeout` first, then keep `checkpoint_completion_target=0.9` (the modern default).

## Real-world usage patterns

- **B2B SaaS, single-region Postgres primary + one hot standby.** Streaming replication over the WAL feeds a synchronous or async standby. WAL is archived to S3 for 30-day PITR. The non-obvious lesson: the standby's `hot_standby_feedback=on` prevents query cancellation on the replica but pushes vacuum lag back onto the primary — a subtle way a reporting replica bloats the primary's WAL.

- **E-commerce, MySQL 8 with Aurora-style multi-AZ + Debezium CDC to Kafka.** InnoDB redo log handles crash recovery; the binlog is consumed by Debezium into Kafka for downstream analytics and search indexing. The lesson: binlog retention and Kafka consumer lag are coupled — if a Debezium consumer lags, binlog files pile up and can gate purge. Alert on both together.

- **Mobile app with local SQLite + Litestream to object storage.** `journal_mode=WAL` for concurrent reads; Litestream tails the WAL file and streams frames to S3, giving continuous backup with second-level RPO from an embedded database. The lesson: SQLite's checkpointing must be cooperative with the replicator, or checkpoints will race Litestream and truncate frames it hasn't uploaded.

- **Fintech ledger, event-sourced service on Kafka.** No traditional RDBMS as the system of record — the Kafka partition log *is* the WAL and the data. Downstream projections rebuild state by replaying. The lesson: you inherit every WAL problem (retention, compaction trade-offs, replay time bounds recovery) at the application layer, and now they're your team's problem, not the DB vendor's.

- **Control plane on etcd/Raft.** Each cluster member persists a Raft log — a replicated WAL. Compaction via snapshots is the checkpoint. The lesson: an under-sized `--snapshot-count` makes the WAL huge and restart-recovery slow; the same knob shape you see in every WAL-based system.

## Operational checklist

- Monitoring: is `pg_wal/` (or `ib_logfile*` / `-wal` file) size graphed with an alert at 70/85/95%?
- Monitoring: is replication lag alarmed in **bytes** per replica *and* per replication slot, not just seconds?
- Monitoring: does `pg_stat_archiver.last_failed_wal` (or MySQL `SHOW BINARY LOGS` growth) page on-call?
- Failure handling: is there a documented runbook for "primary is read-only because pg_wal is full" — including how to drop a stuck slot?
- Failure handling: has PITR been *actually restored* into a scratch environment within the last 90 days?
- Security: is the WAL archive bucket encrypted, versioned, and locked down? Anyone with read access can reconstruct the whole database.
- Cost: are you paying egress on cross-region WAL shipping? Compression + regional replicas often halve the bill.
- Cost: is `wal_compression` (or `binlog_transaction_compression`) evaluated on write-heavy instances?
- Onboarding: does day-one docs explain the difference between "log", "binlog", "redo log", "WAL", and "archive" for your specific engine? These are the terms new hires will confuse.
- Onboarding: does every engineer know the one command that lists replication slots and their retained WAL? (`SELECT slot_name, active, wal_status, pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)) FROM pg_replication_slots;`)

## How this topic typically evolves in a codebase

Teams start with defaults. A managed Postgres or MySQL instance is provisioned, nobody touches WAL settings, and it works fine up to a few hundred TPS. The first painful learning is usually a disk-full incident — a runaway replication slot, a failing archive command, or a checkpoint storm during a write burst. That incident is where the operations team stops treating WAL as invisible.

The second phase is when the team wants CDC. Someone stands up Debezium or a Kafka Connect connector, and suddenly WAL is a *product*: it powers search indexing, analytics, cache invalidation, and audit. This is where logical decoding, replication slot management, and schema-change coordination stop being optional. It's also where the first "the CDC pipeline is down and now our primary disk is filling" outage happens.

The third phase — usually only at real scale — is when teams start caring about WAL cost and latency directly: WAL compression, dedicated NVMe for `pg_wal/`, cross-region archive bandwidth budgets, custom RPO tiers per table, or moving to a system where the log is the primary abstraction (Kafka-backed event sourcing, Aurora's log-only storage layer, FoundationDB). At that point the WAL has been promoted from implementation detail to load-bearing architecture, and the engineers who understand it are the ones designing the next system.

## Further reading

- [PostgreSQL WAL Internals](https://www.postgresql.org/docs/current/wal-internals.html) — first-party spec, short, no fluff. Read this before any tuning.
- [SQLite Write-Ahead Logging](https://www.sqlite.org/wal.html) — the clearest single-page WAL explainer in existence, from the engine's author.
- [Mastering Postgres Replication Slots — Gunnar Morling](https://www.morling.dev/blog/mastering-postgres-replication-slots/) — practical guide to the #1 WAL production incident (stuck slots + disk fill).
- [ARIES paper (Mohan et al., 1992)](https://cs.stanford.edu/people/chrismre/cs345/rl/aries.pdf) — the algorithm everything you use implements. Dense, but foundational.
- [MySQL InnoDB Redo Log](https://dev.mysql.com/doc/refman/8.0/en/innodb-redo-log.html) — the redo/undo/binlog distinction explained by the source.
- [Litestream — how it streams SQLite WAL frames](https://litestream.io/how-it-works/) — the cleanest small-scale example of "the WAL is the replication stream."
