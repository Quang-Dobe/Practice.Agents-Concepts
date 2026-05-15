# Write-Ahead Log — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In any SaaS backend running Postgres or MySQL, the WAL is the file directory that fills up at 3 a.m. and pages you. You will not write WAL code, but you will tune `wal_level`, watch `pg_wal/` size, decide whether `synchronous_commit=off` is acceptable, and explain to product why their CDC consumer is now blocking the primary.

In **data platform / streaming** work, the WAL becomes the source. Debezium tails Postgres logical decoding or MySQL binlog; Materialize, ClickHouse, Snowflake all subscribe. The WAL stops being a recovery artifact and becomes the contract between OLTP and the rest of the company.

In **embedded / mobile**, the WAL shows up the moment you flip `PRAGMA journal_mode=WAL` on SQLite — suddenly your readers stop blocking writers, but you also inherit a `-wal` and `-shm` sidecar file that surprises backup scripts and crash reporters.

In **disaggregated cloud databases** (Aurora, Neon, AlloyDB), the WAL *is* the storage protocol. You don't see it, but every query latency cliff is somewhere downstream of it.

## Best practices

### 1. Put WAL on its own device, or at least its own filesystem
**Do:** On bare metal or VMs with multiple disks, dedicate one NVMe to WAL/redo and a separate device to data files. On managed cloud, use a provisioned-IOPS volume sized for your peak WAL bytes/sec.
**Why:** WAL is sequential, data files are random. Mixing them on one spindle makes both slower and makes checkpoint storms catastrophic — commit latency rises just when you can least afford it.
**Avoid:** A single 100 GB root volume holding OS, data, and WAL — fine for a demo, a foot-gun in production.

### 2. Pick a `synchronous_commit` / `innodb_flush_log_at_trx_commit` setting *deliberately*, per workload
**Do:** Default to maximum durability (`synchronous_commit=on`, `innodb_flush_log_at_trx_commit=1`, `sync_binlog=1`). Relax it only for explicitly tagged "best-effort" workloads (analytics ingest, audit trails the upstream can replay), and isolate them in their own session or database role.
**Why:** With `innodb_flush_log_at_trx_commit=2`, an OS crash loses ~1 s of "committed" writes — silently. Finance and inventory teams will not forgive that. With Postgres `synchronous_commit=off`, you can lose up to `wal_writer_delay` (200 ms default) of acknowledged commits on crash; the DB stays consistent but your users do not.
**Avoid:** Flipping the global to `off`/`2` "for performance" without measuring whether group commit was already saturating the fsync path. It almost always was.

### 3. Always set `max_slot_wal_keep_size` (Postgres) and monitor slot lag
**Do:** Set `max_slot_wal_keep_size` to a finite ceiling (e.g. 50–100 GB depending on free space). Alert on `pg_replication_slots.wal_status = 'extended'` and on `confirmed_flush_lsn` going stale.
**Why:** The default is `-1` (unlimited). One forgotten logical replication slot — a Debezium consumer that crashed, a `pg_recvlogical` test someone left running — will pin WAL indefinitely until `pg_wal/` fills the disk and the database refuses all writes. This is the single most common "Postgres died at 3 a.m." story of the last five years.
**Avoid:** Treating slot creation as a side effect of `CREATE SUBSCRIPTION` and never tracking who owns each slot.

### 4. Tune checkpoints for the workload, not for the docs
**Do:** Aim for checkpoints to be *timed*, not *forced by WAL volume*. In Postgres: set `checkpoint_timeout` to 15–30 minutes, `max_wal_size` large enough that 90%+ of checkpoints are timed (check `pg_stat_checkpointer.num_timed` vs `num_requested`), and `checkpoint_completion_target=0.9` to spread I/O.
**Why:** Forced checkpoints under load create write storms — the kernel flushes hundreds of MB of dirty pages at once, fsyncs stall, p99 latency spikes. Full-page writes also balloon WAL volume right after a checkpoint; longer checkpoint intervals mean fewer "first writes" and lower WAL bytes/sec overall.
**Avoid:** Leaving `max_wal_size` at the 1 GB default on a write-heavy 8-core instance.

### 5. Enable `wal_compression` (or `binlog_transaction_compression`) on write-heavy systems
**Do:** Turn on `wal_compression` (Postgres 14+ supports `lz4` and `zstd` in addition to `pglz`). For MySQL 8.0.20+, consider `binlog_transaction_compression=ON`.
**Why:** Full-page writes dominate WAL volume on update-heavy workloads. Compressing them at the source cuts WAL bytes by 2–5x, which directly reduces archive bandwidth, replica catch-up time, and `pg_wal/` disk pressure.
**Avoid:** Enabling it on tiny instances with spare disk but no spare CPU — measure first.

### 6. Verify your storage actually honors fsync
**Do:** Before going to production on new hardware or a new cloud volume type, run `pg_test_fsync` and `diskchecker.pl` (Postgres wiki). Pull the power on a non-prod node mid-load and confirm no committed rows are missing.
**Why:** Consumer SSDs, some virtualized block devices, and misconfigured RAID controllers acknowledge fsync before data is on stable media. The 2018 Linux "fsyncgate" also showed the page cache can silently drop dirty pages on writeback error — Postgres now `PANIC`s on fsync failure for this reason.
**Avoid:** Trusting marketing. "Enterprise SSD" is not a durability claim.

### 7. Treat WAL as a stream, not just a recovery artifact
**Do:** If you need CDC, use logical replication (`pgoutput`, `wal2json`) or MySQL row-based binlog through Debezium, instead of dual-writing from the app to Kafka. Version the schema of consumed events.
**Why:** Dual writes (app writes to DB and Kafka) cannot be made atomic without exactly the kind of two-phase commit your team is trying to avoid. Tailing the WAL gives you atomicity for free — the DB commit *is* the event.
**Avoid:** "Outbox table polled every 5 seconds" as a permanent solution. Fine as a first step, painful at 10k writes/sec.

### 8. Monitor WAL generation rate as a first-class metric
**Do:** Track bytes/sec into the WAL (`pg_stat_wal.wal_bytes` delta, MySQL `Innodb_os_log_written` delta). Alert when it doubles week-over-week with no traffic change.
**Why:** A sudden jump usually means a new ORM-induced full-table update, a missing `WHERE`, or someone disabled HOT updates by indexing every column. WAL volume is the cheapest early-warning signal for write pathologies.
**Avoid:** Only watching disk free space — by the time it moves, the bad query has been running for hours.

### 9. Separate physical replication lag from logical replication lag in alerts
**Do:** Page on physical-replica byte lag (`pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn)`) at one threshold, and on logical-slot lag at another, much tighter, threshold tied to your disk free space.
**Why:** A physical replica falling 1 GB behind is fine. A logical slot 1 GB behind is fine. A logical slot 200 GB behind means you have hours, not days, before the primary dies.
**Avoid:** One global "replication lag" dashboard that hides which slot is the culprit.

### 10. Archive WAL to object storage, and *test the restore*
**Do:** Ship WAL segments to S3/GCS via `wal-g`, `pgBackRest`, or Barman. At least quarterly, restore a base backup + WAL replay to a fresh instance and check a known row.
**Why:** A broken `archive_command` (network blip, expired IAM creds, full bucket) silently retains WAL on the primary forever — same disk-full failure mode as orphan slots. And an untested restore is a Schrödinger backup.
**Avoid:** `archive_command = 'cp %p /mnt/nfs/...'` to an NFS mount nobody monitors.

## Anti-patterns to recognize

- **`synchronous_commit=off` as the cure for slow commits**: Looks like a free 5x throughput win on benchmarks. In production it hides a missing index or undersized disk while quietly setting up a future "we lost the last 200 ms of orders" incident. Fix the underlying I/O first; relax durability only for explicitly opt-in workloads.
- **Mixing data and WAL on the same disk under heavy load**: Looks fine on a fresh box. Under sustained writes, checkpoint flushes and WAL fsyncs queue behind each other on the same I/O scheduler, and p99 commit latency goes from 2 ms to 200 ms. Move WAL to its own device or volume.
- **Logical replication slot left behind by a deleted consumer**: The subscriber app gets decommissioned; nobody drops the publication slot. WAL accumulates silently for weeks. Always inventory slots, give them owner tags, and set `max_slot_wal_keep_size`.
- **Treating the binlog as the source of truth for CDC without `sync_binlog=1`**: Default `sync_binlog=0` lets MySQL ack a commit before the binlog hits disk. Your Kafka consumer can see events the primary forgets after a crash. Set `sync_binlog=1` whenever the binlog is wired to anything downstream.
- **Cranking `max_wal_size` huge to "stop checkpoints"**: Recovery time grows linearly with WAL since the last checkpoint. A 100 GB `max_wal_size` means crash recovery might take 20+ minutes. Time it on a representative box before you set it.
- **Running on a filesystem with broken fsync semantics**: ZFS-on-Linux with `sync=disabled`, `ext4` with `data=writeback`, or some QEMU configurations all silently weaken durability. The database has no way to know. Read the storage stack's docs and test with power-pulls.
- **Inspecting WAL with `cat` or `strings`**: Not just useless — physiological records aren't readable that way. Use `pg_waldump`, `mysqlbinlog`, or `ldb`.

## Real-world usage patterns

**1. Multi-tenant SaaS with read replicas.** A B2B app on Postgres 16, single primary, three streaming replicas across AZs. WAL is shipped synchronously to one replica (`synchronous_standby_names`) for zero-RPO failover, asynchronously to the others for read scaling. *Non-obvious lesson:* the synchronous replica is the latency floor for every commit on the primary. If it GC-pauses or its network blips, your write API blips with it. Always have at least two candidate sync standbys (`ANY 1 (s1, s2)`).

**2. E-commerce CDC pipeline.** Postgres primary, Debezium connector reading via `pgoutput`, events flowing to Kafka, consumed by a search indexer and a data warehouse. *Non-obvious lesson:* Debezium's slot is a hard dependency of the primary's health. Treat the connector as a tier-1 service — alert on slot lag in MB, not in seconds, and give it more headroom than you think you need during schema migrations (which generate big WAL bursts).

**3. Embedded analytics in a desktop app.** A cross-platform app stores user data in SQLite with `journal_mode=WAL` and `synchronous=NORMAL`. Reads never block writes; writes are batched. *Non-obvious lesson:* the `-wal` file can grow surprisingly large if a long-running read transaction prevents checkpointing. Periodically run `PRAGMA wal_checkpoint(TRUNCATE)` from an idle path, and never copy the `.db` file alone — back up `.db`, `-wal`, and `-shm` together or use the SQLite backup API.

**4. Financial ledger on MySQL 8.** InnoDB with `innodb_flush_log_at_trx_commit=1`, `sync_binlog=1`, group commit enabled, binlog shipped to a parallel-replica fleet. *Non-obvious lesson:* group commit (`binlog_group_commit_sync_delay`) trades a few hundred microseconds of tail latency for a 5–10x throughput floor under burst load. Tune it deliberately; the default of 0 leaves performance on the table.

**5. Disaggregated cloud Postgres (Aurora / Neon-style).** Compute nodes ship WAL records to a distributed storage layer; pages are reconstructed on demand from the log. *Non-obvious lesson:* the WAL is no longer a local file you can `ls`. Operational intuitions about "checkpoint pressure" and "WAL on its own disk" mostly don't apply — but logical replication slots and full-page-write volume still matter, and the same disk-full-via-stuck-slot story shows up as a control-plane error instead of a filesystem one.

## Operational checklist

- **Monitoring:** WAL bytes/sec, `pg_wal/` (or `#innodb_redo/`) disk usage %, replication lag per slot in bytes, checkpoint timed-vs-requested ratio, archive backlog count, fsync time p99.
- **Failure handling:** What happens if `archive_command` fails for 1 hour? For 24 hours? Is this alerted and runbooked? Has a restore from archive actually been performed in the last 90 days?
- **Slot hygiene:** Every replication slot has a named owner, a finite `max_slot_wal_keep_size`, and an alert wired to slot lag. Stale slots are reaped in CI for non-prod environments.
- **Durability assumptions:** `synchronous_commit` / `innodb_flush_log_at_trx_commit` settings are documented per environment. The on-call knows which workloads can lose 200 ms of commits and which can't.
- **Storage assumptions:** fsync behavior on the production volume type has been verified, not assumed. The Postgres version is new enough to PANIC on fsync error (9.4+ back-patched).
- **Cost:** WAL archive storage growth is tracked. A logical replica that suddenly streams 10x more bytes (e.g. after an `UPDATE` migration) does not silently 10x your egress bill.
- **Disaster recovery:** Documented RPO and RTO. Both have been measured, not estimated, against a representative backup + WAL replay.
- **Onboarding:** A new engineer knows where WAL lives, how to read it (`pg_waldump`), how to list slots, and what "disk full because of WAL" looks like before they go on-call.

## How this topic typically evolves in a codebase

**Early stage:** A single Postgres or MySQL with default settings. Nobody touches WAL config. The first encounter is usually a panicked "the disk is full, why" — almost always a forgotten replication slot, a failed `archive_command`, or a `wal_keep_size` left at a debugging value.

**Mid stage:** Read replicas appear. Now there is replication lag to monitor, `synchronous_standby_names` to think about, and the first real conversation about RPO. Teams discover `pg_stat_wal`, start tuning checkpoint parameters, and learn that "WAL on its own disk" is not optional past a few hundred writes/sec.

**Late stage:** The WAL becomes a *product surface*. CDC pipelines feed Kafka, the data team owns a Debezium connector, and downstream consumers (search, analytics, ML features) depend on logical decoding. The painful migration point is usually from physical replication to logical replication for a major version upgrade or for fanning out to non-Postgres consumers — schema evolution, slot ownership, and replica identity (`REPLICA IDENTITY FULL`) all become recurring sources of incidents. Past this point teams typically formalize WAL/CDC as its own platform service with explicit SLOs, owners, and tests.

## Further reading

- [PostgreSQL: `max_slot_wal_keep_size` (official docs)](https://www.postgresql.org/docs/current/runtime-config-replication.html) — the one parameter that prevents the most common "WAL filled the disk" outage.
- [EnterpriseDB — PostgreSQL 13: Don't let slots kill your primary](https://www.enterprisedb.com/blog/postgresql-13-dont-let-slots-kill-your-primary) — practical walkthrough of the inactive-slot disaster and how `max_slot_wal_keep_size` changes the failure mode from "DB down" to "slot lost."
- [PostgreSQL wiki — Fsync Errors (fsyncgate)](https://wiki.postgresql.org/wiki/Fsync_Errors) — the 2018 incident that changed how every major DB treats fsync return codes.
- [PostgreSQL wiki — Full page writes](https://wiki.postgresql.org/wiki/Full_page_writes) — why your WAL volume spikes right after a checkpoint and what to do about it.
- [Debezium documentation — PostgreSQL connector](https://debezium.io/documentation/reference/stable/connectors/postgresql.html) — the canonical reference for tailing Postgres WAL as a change stream, including the operational gotchas around slots and replica identity.
- [MariaDB — Binary Log Group Commit and InnoDB Flushing Performance](https://mariadb.com/docs/server/server-usage/storage-engines/innodb/binary-log-group-commit-and-innodb-flushing-performance) — the clearest explanation of how group commit, `innodb_flush_log_at_trx_commit`, and `sync_binlog` interact under load.
