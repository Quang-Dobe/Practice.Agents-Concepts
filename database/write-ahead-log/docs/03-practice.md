# Write-Ahead Log — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

You meet the WAL the first time your Postgres primary refuses writes because `pg_wal/` filled the disk, or the first time your MySQL replica lags by twelve hours and you cannot figure out why. Nobody talks about the WAL when the system is healthy — it is the on-call topic that only surfaces when a knob is wrong, a slot is leaking, or a checkpoint is stalling your P99.

You also meet it as a *product surface*. Any change-data-capture pipeline (Debezium, Fivetran, Materialize) sits on top of a database's WAL. Any Raft-backed control plane (etcd, Consul, CockroachDB) writes to a WAL on the hot path of every write. Cloud-native OLTP (Aurora, AlloyDB, Neon) has literally disaggregated the WAL into a network service. Once you know the pattern, you see it everywhere your writes are supposed to survive a crash.

Third place you meet it: capacity planning. WAL bytes-per-second is the number that sets your replication bandwidth, your archive-storage bill, and your worst-case RPO. Get it wrong and you find out during a failover.

## Best practices

### 1. Match `synchronous_commit` / `innodb_flush_log_at_trx_commit` to the business, not the benchmark
**Do:** Default to `synchronous_commit = on` (Postgres) and `innodb_flush_log_at_trx_commit = 1` (InnoDB) for anything transactional. Only relax to `off` / `2` for a specific workload — ingestion buffers, metrics, ephemeral queues — after you have measured the throughput win.
**Why:** `synchronous_commit = off` loses up to ~3 × `wal_writer_delay` of committed transactions on crash (~600 ms with defaults, per the Postgres docs). `innodb_flush_log_at_trx_commit = 2` survives a `mysqld` crash but loses ~1 s of transactions on OS crash or power loss.
**Avoid:** A cluster-wide flag flip because "it made TPC-C faster." Scope the relaxation per-session for the write path that can tolerate it.

### 2. Cap replication-slot WAL retention, always
**Do:** Set `max_slot_wal_keep_size` to something finite (5–20 GB is a common starting point on a mid-size cluster). Alert when any slot's retained bytes crosses 50 % of that cap.
**Why:** The default `-1` lets a single abandoned logical slot pin the entire `pg_wal/` volume until the disk fills and the primary goes read-only. This is the single most common WAL incident in production.
**Avoid:** Trusting that "nobody would leave a slot behind." Dev teams create slots for one-off Debezium tests and forget them.

### 3. Tune checkpoints so they trickle, not spike
**Do:** Keep `checkpoint_completion_target = 0.9` (Postgres default since 9.5) and size `max_wal_size` so `checkpoint_timeout` (default 5 min) is the trigger, not the size limit. Similarly on InnoDB: `innodb_io_capacity` and `innodb_io_capacity_max` should reflect the actual sustainable IOPS of the data volume.
**Why:** A size-triggered checkpoint bursts write I/O to hit the deadline. That is the classic "P99 spike every N minutes" pattern you see on write-heavy OLTP.
**Avoid:** Turning checkpoints off or pushing `checkpoint_timeout` to hours to "get more throughput." You just shifted the pain to a multi-hour crash-recovery replay.

### 4. Archive the WAL from day one if you claim PITR
**Do:** Wire up `archive_mode = on` with a real `archive_command` (or use `pgBackRest`, `WAL-G`, `Barman`). The command must return non-zero on failure, and you must monitor `pg_stat_archiver` for `last_failed_time`.
**Why:** A silently failing `archive_command` returns 0, Postgres thinks the segment is safe, and it recycles the file. You discover this the day you need PITR and cannot rebuild the archive chain.
**Avoid:** A shell one-liner with `cp ... || true`. Every WAL-archive outage I have seen started with an `|| true` swallowing an S3 5xx.

### 5. Kill long-running transactions before they kill your WAL
**Do:** Set `idle_in_transaction_session_timeout` (Postgres) and `statement_timeout` at the pool or role level. Monitor `pg_stat_activity` for transactions older than, say, five minutes.
**Why:** An open transaction pins `xmin`, which pins WAL segments needed to keep the transaction's snapshot valid. It also blocks vacuum. A forgotten `BEGIN;` in a `psql` session is enough to fill a 500 GB disk over a weekend.
**Avoid:** ORMs that hold a connection with an open transaction across a slow external HTTP call. That is a WAL-pinning bug with a Java stack trace.

### 6. Treat replica lag as two numbers, not one
**Do:** Track lag in *bytes* (`pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn)`) **and** in *seconds* (`now() - pg_last_xact_replay_timestamp()`). Alert on both, and on divergence between them.
**Why:** Bytes tell you disk-and-network health; seconds tell you replay throughput. A replica that is 0 bytes behind but 10 seconds behind is stuck on a single-threaded replay of a large index build. A replica that is 5 GB behind but 0.5 seconds behind is bandwidth-starved.
**Avoid:** A single "lag" metric that is really `now() - replay_timestamp`. It hides all the interesting failure modes.

### 7. Enable full-page writes; enable WAL compression if bandwidth is the pain
**Do:** Leave `full_page_writes = on` (Postgres default). Turn on `wal_compression = on` if you see FPI dominating the WAL bytes-per-second (visible in `pg_stat_wal`).
**Why:** Full-page images heal torn writes after power loss on 4 KB-sector storage. Compression trades CPU for ~30–70 % smaller WAL, which flows into cheaper archive storage and less replication bandwidth.
**Avoid:** Disabling FPI to "save WAL" on a system where you have not confirmed the storage stack guarantees atomic 8 KB writes. That is silent-corruption territory.

### 8. Verify `fsync` actually flushes
**Do:** Run `pg_test_fsync` (or equivalent on your storage) and compare the ops/sec to your drive's spec sheet. Confirm the drive has power-loss protection (PLP) if it is a consumer SSD masquerading as enterprise.
**Why:** A lying `fsync` — hardware that acks before the write reaches non-volatile media — voids every durability promise the WAL makes. This is not theoretical; it is why the Postgres project shipped the "fsyncgate" changes in 2018.
**Avoid:** Trusting cloud block storage without checking. EBS gp3, GCP PD, Azure Premium SSD all honour fsync, but layered stacks (ZFS on top, LVM snapshots, network filesystems) can break the guarantee.

## Anti-patterns to recognize

- **`fsync = off` in production**: Someone benchmarks with fsync off, sees a 10× speedup, and never puts it back. On the next power event you lose committed transactions and, worse, may get on-disk corruption because the WAL invariant is now broken. Alternative: use `synchronous_commit = off` for the specific write path that tolerates loss, and leave `fsync = on`.

- **Dropping a replication slot to "free space"**: Disk is full, the slot is holding 200 GB of WAL, someone drops it. Space is freed, the replica reconnects, discovers its `restart_lsn` is gone, and needs a full re-clone from `pg_basebackup`. Alternative: fix the consumer, or accept the re-clone consciously.

- **Treating the WAL as a general-purpose event log**: A team plugs Debezium into Postgres and starts using logical decoding as their company-wide event bus. Then a schema change breaks decoding, a slow consumer pins WAL for hours, and the primary destabilises. Alternative: use the WAL to *feed* Kafka (a proper event log with independent retention), not as Kafka.

- **Uncoordinated `checkpoint_timeout` and `max_wal_size`**: `max_wal_size` is too small, so size-triggered checkpoints fire constantly, generating a torrent of full-page images (every first-touch of a page after checkpoint gets an 8 KB FPI). WAL volume triples, replication lags, archive bill spikes. Alternative: size `max_wal_size` so time triggers dominate under peak load.

- **Long-running analytics transactions on the primary**: A BI query runs for two hours inside a transaction. WAL segments and dead tuples accumulate the entire time. Alternative: run analytics on a hot standby with `hot_standby_feedback = on` (and understand *that* also pins WAL on the primary — pick your poison).

- **Assuming logical replication is free**: Enabling `wal_level = logical` roughly doubles WAL volume for update-heavy workloads because Postgres logs before-images for changed columns. If you flip the knob without measuring, your archive storage bill doubles overnight.

## Real-world usage patterns

- **High-volume SaaS OLTP on Postgres**. A B2B SaaS running ~5k writes/s on a 3-node cluster with sync replication (`synchronous_commit = remote_apply` to one standby, async to the other). Non-obvious lesson: the sync standby's `fsync` latency becomes the primary's commit latency. A single degraded EBS volume on the standby raises every write's P99 on the primary. Monitor the standby's disk as if it were the primary's.

- **CDC pipeline into a data warehouse**. Fintech uses Debezium reading a Postgres logical slot, feeding Kafka, landing in Snowflake. Non-obvious lesson: the slot is a hard dependency of the primary's disk health. When the warehouse team pauses ingestion for a "quick" schema migration, WAL accumulates at 40 GB/hour. They now have a runbook: never pause the consumer without first increasing the disk or dropping-and-re-snapshotting the slot.

- **Multi-region PITR with `WAL-G`**. E-commerce backend ships WAL to S3 every 60 s, takes a base backup nightly. RPO is ~60 s, RTO is ~15 min for a 500 GB database. Non-obvious lesson: restoring PITR is I/O-bound on WAL replay throughput, not on network. Parallel `restore_command` (WAL-G's `--fetch-parallelism`) is the single biggest RTO knob.

- **Raft-backed control plane (etcd-shaped)**. A Kubernetes-style control plane where every API write is a Raft append. Non-obvious lesson: the WAL fsync latency on the *slowest* Raft peer sets the write latency of the entire cluster. One noisy-neighbour VM on one etcd member makes every `kubectl apply` slow.

- **RocksDB inside a stateful streaming job**. Flink/Kafka Streams uses RocksDB's WAL for exactly-once state. Non-obvious lesson: turning off the RocksDB WAL is safe *only if* the framework can rebuild state from an upstream source of truth (the Kafka log) on failure. Otherwise you have silently opted out of durability.

## Operational checklist

- **Monitoring**: WAL bytes/sec written, checkpoint duration, fsync p99 latency, replica lag in both bytes and seconds, `pg_stat_archiver.last_failed_time`, per-slot `pg_wal_lsn_diff` retained bytes.
- **Failure handling**: What happens if `archive_command` fails for one hour? For 24 hours? Is that tested in a game day?
- **Slot hygiene**: Is there an alert for slots older than N hours with no consumer? Is `max_slot_wal_keep_size` set?
- **Recovery drill**: Have you actually run PITR to a random timestamp in the last 90 days? RTO measured, not estimated?
- **Security foot-gun**: WAL archives contain every row you have ever written. Is the S3 bucket encrypted, versioned, and access-logged?
- **Cost**: WAL archive size per day, tracked and forecast. Enabling logical replication or dropping `wal_compression` can double it silently.
- **Onboarding**: Can a new SRE, on day one, find the runbook for "primary disk 85 % full, WAL is growing"? That runbook should list the three usual causes (stuck slot, failing archiver, long transaction) in order.

## How this topic typically evolves in a codebase

Teams start by ignoring the WAL entirely. Postgres works, MySQL works, defaults are fine, checkpoints happen invisibly. The first WAL incident is almost always a disk-full page at 3 a.m. caused by one of the three usual suspects, and the team learns that `pg_wal/` is a thing.

Stage two: the team enables WAL archiving for backups, adds a replica, and starts caring about replication lag. This is where they first hit the "single-threaded WAL replay" ceiling and either accept it (`hot_standby_feedback`, careful long-query hygiene) or route analytical load elsewhere.

Stage three: the team plugs Debezium into a logical slot for CDC. This is the painful migration point — WAL is no longer just a durability primitive, it is a product surface with SLAs. Slot leaks, `wal_level = logical` doubling WAL volume, decoding lag correlating with primary CPU — all of it becomes on-call territory. The mature end-state is treating WAL bytes/sec as a first-class capacity metric alongside CPU and memory, and treating every consumer of the WAL (replicas, archivers, CDC slots) as a dependency that can, and eventually will, take the primary down if not bounded.

## Further reading

- [PostgreSQL: Reliability and the Write-Ahead Log](https://www.postgresql.org/docs/current/wal.html) — the canonical chapter; short, precise, and the authoritative source on every `wal_*` knob.
- [PostgreSQL: Asynchronous Commit](https://www.postgresql.org/docs/current/wal-async-commit.html) — the exact durability window `synchronous_commit = off` buys you, from the source.
- [EDB — PostgreSQL 13: Don't let slots kill your primary](https://www.enterprisedb.com/blog/postgresql-13-dont-let-slots-kill-your-primary) — the reference post on `max_slot_wal_keep_size` and why the old default was dangerous.
- [Gunnar Morling — Mastering Postgres Replication Slots](https://www.morling.dev/blog/mastering-postgres-replication-slots/) — practical Debezium-oriented guidance on slot lifecycle in production.
- [Cybertec — Why does my pg_wal keep growing?](https://www.cybertec-postgresql.com/en/why-does-my-pg_wal-keep-growing/) — the three usual causes, in the order you should check them at 3 a.m.
- [MySQL Reference — innodb_flush_log_at_trx_commit](https://dev.mysql.com/doc/refman/8.0/en/innodb-parameters.html#sysvar_innodb_flush_log_at_trx_commit) — the InnoDB counterpart to `synchronous_commit`, with the exact loss window per setting.
- Mohan et al., *ARIES* (ACM TODS 1992) — the original paper. Long, but the recovery algorithm every RDBMS still uses is defined here.
