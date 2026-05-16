# MVCC (Multi-Version Concurrency Control) — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, MVCC is the invisible substrate every ORM call relies on — and the first thing you blame when "the database got slow at 3am." You meet it for real the day a long-running cron job pins `xmin`, autovacuum stops reclaiming dead tuples, and a 200 GB table balloons to 600 GB over a weekend. Or the day a payment-processing service starts double-charging customers because two requests both passed a balance check under snapshot isolation and committed independently.

In an analytics-heavy product, MVCC is the reason you can run a 40-minute report against the live OLTP primary without locking out writers — and the reason your DBA still tells you not to. In any distributed SQL stack (CockroachDB, Spanner, Yugabyte), MVCC over HLC timestamps is what lets you reason about cross-region snapshot reads at all.

The recurring theme: MVCC is forgiving until it isn't, and the failure modes always trace back to either (a) someone held a transaction open too long, (b) someone assumed snapshot isolation was serializable, or (c) someone fought the garbage collector.

## Best practices

### 1. Default to READ COMMITTED, escalate per query
**Do:** Run OLTP traffic at READ COMMITTED. Reach for `REPEATABLE READ` only when a single transaction makes multiple reads that must agree (e.g. a report aggregating across three tables). Reach for `SERIALIZABLE` only for money-moving or invariant-protecting transactions where write skew would corrupt state.
**Why:** Higher isolation levels pin snapshots longer and increase serialization-failure rate. Paying that cost on every read is wasteful; not paying it on the 1% that needs it is a bug.
**Avoid:** Setting `SERIALIZABLE` globally "to be safe" — you will get `40001` storms under contention and no one will know how to retry.

### 2. Keep transactions short — measured in milliseconds, not minutes
**Do:** Open the transaction as late as possible, commit as soon as the last write lands. Move all validation, I/O, and external calls outside the `BEGIN ... COMMIT` block. Target p99 transaction duration under 100ms for OLTP.
**Why:** Every live transaction pins the global `xmin` horizon. A single forgotten `BEGIN` from 10 minutes ago tells Postgres "any dead tuple newer than 10 minutes ago might still be needed" — autovacuum gives up, bloat accumulates, and the InnoDB history list explodes the same way.
**Avoid:** Wrapping an HTTP call to Stripe inside a database transaction. The transaction will be open for the network round-trip and you have just handed the DB a 2-second xmin pin per request.

### 3. Never hold a transaction across user think-time or external I/O
**Do:** Read data, close the transaction, let the user think, then start a *new* transaction with `SELECT ... FOR UPDATE` or an optimistic version check when they submit.
**Why:** This is the #1 cause of `idle in transaction` pathology. A connection that returned a result set to the app but hasn't been told to `COMMIT` or `ROLLBACK` is still holding its snapshot. In a connection pool with 200 connections, you only need a handful to stall vacuum across the entire cluster.
**Avoid:** ORM patterns that auto-open a transaction on first query and leave it open until end-of-request. Make transactional scope explicit.

### 4. Configure `idle_in_transaction_session_timeout` aggressively
**Do:** On Postgres, set `idle_in_transaction_session_timeout = '60s'` for service accounts, lower (a few seconds) for interactive logins. Pair with `statement_timeout` and a pool-side `server_idle_timeout` in PgBouncer.
**Why:** This is your seatbelt against a bug in application code or a wedged client. The session gets terminated, the snapshot releases, vacuum can proceed. Yes, the affected request fails — that is the point, you want it to fail loud rather than silently rot the database.
**Avoid:** Leaving it at the default of `0` (disabled) in production. Cybertec and AWS both flag this as the single highest-leverage Postgres tuning for OLTP.

### 5. Use `SELECT ... FOR UPDATE` for read-modify-write on a single row
**Do:** When the logic is "read row, decide based on it, update it," use `SELECT ... FOR UPDATE` inside the transaction. Use `FOR UPDATE SKIP LOCKED` for queue-style workers grabbing the next job.
**Why:** Pure SI lets two transactions both read the same balance, both decide there's enough, both deduct, both commit. `FOR UPDATE` opts that single row into pessimistic locking — the second transaction blocks until the first commits, then re-reads the now-current value.
**Avoid:** Reaching for SERIALIZABLE when one well-placed `FOR UPDATE` would do. Row locks are cheap and predictable; SSI false-positive aborts are not.

### 6. Prefer `INSERT ... ON CONFLICT` over check-then-insert
**Do:** Use `INSERT ... ON CONFLICT (key) DO UPDATE` in Postgres, `INSERT ... ON DUPLICATE KEY UPDATE` in MySQL, or `MERGE` where supported. One statement, no TOCTOU window.
**Why:** "Does the row exist? No? Insert it." is two statements with a snapshot gap between them — under SI two concurrent sessions will both pass the existence check and both try to insert, producing a unique-violation race condition. Upsert collapses the window into a single atomic primitive the engine handles correctly.
**Avoid:** `if not exists: insert()` in application code. It is a bug in every isolation level except true serializable.

### 7. Treat serialization failures as a first-class control-flow case
**Do:** Wrap any `SERIALIZABLE` (and ideally any `REPEATABLE READ`) transaction in a retry loop with bounded attempts and exponential backoff. Postgres SQLSTATE `40001` (`serialization_failure`) and `40P01` (`deadlock_detected`) are *expected*, not bugs.

```python
for attempt in range(5):
    try:
        with conn.transaction(isolation="serializable"):
            do_work()
        break
    except SerializationFailure:
        sleep(backoff(attempt))
else:
    raise
```

**Why:** SSI achieves serializability by aborting one transaction in any "dangerous structure." If the app doesn't retry, the user sees a 500 error for what is in fact a normal, expected outcome of the protocol.
**Avoid:** Logging `40001` as `ERROR` and paging on-call. It is informational at low rates; only alarm on the *rate*.

### 8. Chunk bulk updates into bounded batches
**Do:** For `UPDATE huge_table SET x = ...` touching millions of rows, write a loop that processes 5k–50k rows per transaction, commits, then continues. Use a stable key window (`WHERE id BETWEEN ? AND ?`) so each batch is bounded and resumable.
**Why:** A single transaction updating 100M rows in Postgres writes 100M new tuple versions, marks 100M dead, and holds an xmin pin for hours — bloating the table by 2x and blocking vacuum the whole time. In InnoDB it inflates the undo tablespace catastrophically. Batching keeps each transaction short, lets vacuum/purge run in between, and is restartable after a crash.
**Avoid:** "It's a one-time migration, who cares" — that one-time migration is what causes the 3am page.

### 9. Don't UPDATE columns that didn't change
**Do:** In Postgres specifically, omit unchanged columns from the `UPDATE` set list, or use `WHERE col IS DISTINCT FROM ?` to skip no-op writes. Frameworks like SQLAlchemy with `dirty` tracking already do this; check yours.
**Why:** Postgres's HOT (Heap-Only Tuple) optimisation skips index updates only if no indexed column changed *and* the new tuple fits on the same page. ORMs that send `UPDATE users SET name=?, email=?, ... ` with every column re-set defeat HOT, force a full secondary-index update per row, and balloon bloat on heavily-indexed tables.
**Avoid:** "Update the whole row, it's simpler" — simple to write, expensive to vacuum.

### 10. Leave autovacuum alone, but tune it per-table
**Do:** Keep autovacuum on. For hot tables, lower `autovacuum_vacuum_scale_factor` from the 0.2 default (20% dead) to 0.05 or even 0.02 via `ALTER TABLE ... SET (autovacuum_vacuum_scale_factor = 0.05)`. Raise `autovacuum_vacuum_cost_limit` from the default 200 to 1000+ on modern SSD-backed systems so vacuum doesn't throttle itself into uselessness.
**Why:** The default 20% threshold is fine for a 1M-row table (200k dead = 1 minute of vacuum) and catastrophic for a 1B-row table (200M dead = 1 hour of churn). Vacuum is not your enemy — *late* vacuum is your enemy.
**Avoid:** Disabling autovacuum "to save IO during business hours." You will pay double at 2am when the emergency anti-wraparound vacuum kicks in and can't be cancelled.

### 11. Route long analytical queries off the OLTP primary
**Do:** Send 30-second-plus reporting queries to a read replica, a logical-decoding subscriber, or a separate analytics store (Snowflake, BigQuery, ClickHouse, etc.). Reserve the primary for sub-second OLTP.
**Why:** A long query on the primary pins `xmin` just like a long write transaction does — vacuum cannot reclaim any tuple newer than the query's snapshot. On streaming replicas, set `hot_standby_feedback = on` only if you understand it propagates the same pin back upstream; otherwise raise `max_standby_streaming_delay`.
**Avoid:** Running BI dashboards against the OLTP primary "because it's the freshest data." It's the freshest data right up until it's also the most bloated.

### 12. Know your write-skew shapes
**Do:** Before shipping a multi-row invariant (on-call rotations, ledger debit/credit, inventory reservations across SKUs), explicitly ask: "could two concurrent transactions each read the predicate as satisfied and each act on a *different* row?" If yes, either use SERIALIZABLE with retry or materialise the invariant onto a single row and `FOR UPDATE` it.
**Why:** The canonical doctors-on-call example: rule is "at least one doctor on-call." Two doctors check the rule (sees 2 on-call), both go off-call, both commit, rule violated. SI cannot see this conflict because they updated *different* rows.
**Avoid:** Assuming snapshot isolation == serializable. It does not. Postgres `REPEATABLE READ` allows write skew; only `SERIALIZABLE` rejects it.

## Anti-patterns to recognize

- **The transactional HTTP call**: Wrapping an external API request inside `BEGIN ... COMMIT`. Snapshot pinned for the full network RTT; under load you stall vacuum cluster-wide. Pull the I/O out of the transaction, persist intent before and outcome after.
- **The "wide" UPDATE**: One statement updating tens of millions of rows. Postgres bloats the table by ~100%; InnoDB undo tablespace explodes; either way recovery is a multi-hour outage. Chunk it.
- **The ORM-rewrites-every-column UPDATE**: Saving an entity rewrites all 30 columns including the 28 unchanged ones. HOT misses; every secondary index is updated per row; bloat scales with column count. Configure your ORM to update only dirty fields.
- **Disabling autovacuum**: Done "because vacuum was causing IO spikes." The IO is the cost of correctness — paid daily instead of in one emergency. Tune cost limits up, scale factor down, and let it run.
- **Catching and swallowing 40001**: Application catches `serialization_failure`, logs it, returns success to the user. The write never happened. Always retry, always surface terminal failure.
- **`SELECT FOR UPDATE` on too-broad a query**: `SELECT * FROM orders WHERE status='pending' FOR UPDATE` in a queue worker locks every pending row at once, serialising all workers. Use `LIMIT 1 ... FOR UPDATE SKIP LOCKED` instead.
- **Trusting `count(*) FROM big_table` to be fast under MVCC**: There is no shortcut — Postgres must visit every visible tuple because visibility depends on the caller's snapshot. Cache it, or maintain a counter.
- **Long-running `pg_dump` on the primary**: It opens a `REPEATABLE READ` transaction for the duration. On a high-churn 500 GB database that is hours of pinned xmin. Dump from a replica.

## Real-world usage patterns

**Payments ledger (fintech, ~2k TPS):** Account balance lives in one row per account. Debits use `BEGIN; SELECT balance FROM accounts WHERE id=? FOR UPDATE; ... UPDATE accounts SET balance=? WHERE id=?; INSERT INTO ledger ...; COMMIT;` — pessimistic lock on the contested row, rest of the work fast. SSI was tried; the false-positive abort rate at peak made p99 latency unpredictable. *Lesson: SSI is theoretically cleaner but `FOR UPDATE` on the contested row is more operable.*

**Multi-tenant SaaS CRM (~500 RPS, Postgres):** `customers` table at 80M rows, hot UPDATEs on `last_seen_at`. Initial schema put `last_seen_at` in an index; HOT never fired, bloat hit 3x weekly. Fix: dropped the index (last-seen is queried rarely, by tenant), HOT kicked in, bloat stabilised at <20%. *Lesson: every index you add on a hot-updated column is a tax on MVCC garbage collection.*

**Job queue on Postgres (`SELECT FOR UPDATE SKIP LOCKED`):** 20 workers pull from a `jobs` table. Each runs `SELECT id FROM jobs WHERE state='ready' ORDER BY priority LIMIT 1 FOR UPDATE SKIP LOCKED;` — every worker gets a different job, no contention, no polling-storm. *Lesson: `SKIP LOCKED` is the rare MVCC primitive that turns a hard distributed-systems problem into one line of SQL.*

**Analytics-on-OLTP gone wrong (InnoDB):** BI team ran a 4-hour `REPEATABLE READ` join against the production primary nightly. History list length grew from ~10k to >50M, purge fell behind, undo tablespace hit 400 GB, replica lag spiked, primary IOPS saturated. *Lesson: monitor history list length and treat sustained growth as a P2 page.*

**CockroachDB cross-region writes:** App writes in `us-east`, reads immediately in `eu-west` — sometimes gets a `RETRY_WRITE_TOO_OLD` due to clock-skew uncertainty intervals in HLC-based MVCC. *Lesson: distributed MVCC pushes retry handling out of the optional-pile and into the mandatory-pile; every transaction needs an idempotent retry wrapper.*

## Operational checklist

- **Postgres long transactions:** Alert on `pg_stat_activity` rows where `state='idle in transaction'` AND `xact_start < now() - interval '5 minutes'`. Page on any row > 30 minutes.
- **Postgres xmin horizon age:** Monitor `SELECT max(age(backend_xmin)) FROM pg_stat_activity;` — alert above 50M, page above 200M.
- **Postgres bloat:** `pg_stat_user_tables.n_dead_tup` per table; install `pgstattuple` and chart `dead_tuple_percent` weekly on top-20 hottest tables.
- **Postgres replication slots:** Inactive logical slots pin xmin too. Alert on `pg_replication_slots.active = false` for any slot older than an hour.
- **InnoDB history list length:** Chart `History list length` from `SHOW ENGINE INNODB STATUS` (or `trx_rseg_history_len` in Performance Schema). Warn at 100k sustained, page at 1M.
- **Serialization-failure rate:** Count SQLSTATE `40001` per minute per service. A baseline of a few per minute is fine; a spike to thousands means a hot predicate has become contended — investigate before users notice.
- **Deadlocks:** `pg_stat_database.deadlocks` (Postgres) or `Innodb_deadlocks` (MySQL). Should be near-zero in healthy systems.
- **`idle_in_transaction_session_timeout` set?** Code review: is it set on every Postgres instance, including dev and staging, to a sane value (60s service, 5s interactive)?
- **Retry wrappers in place?** Every `SERIALIZABLE` transaction must be in a retry loop. Grep for `isolation_level=SERIALIZABLE` and confirm.
- **Autovacuum tuned per-hot-table?** Did anyone `ALTER TABLE ... SET (autovacuum_vacuum_scale_factor=...)` on the top-5 hottest tables, or are they still on the global 0.2 default?
- **Anti-wraparound headroom (Postgres):** `SELECT datname, age(datfrozenxid) FROM pg_database;` should sit well under 200M. Alert at 1B, page at 1.5B.

## How this topic typically evolves in a codebase

**Year one:** No one thinks about MVCC. The default isolation level works, the database is small, autovacuum keeps up. Transactions are wrapped automatically by the ORM and no one notices their boundaries.

**Year two:** Tables hit ~100M rows. Someone runs a 10M-row backfill in one transaction, the next morning the table is 2x its previous size and queries are slower. The team learns about VACUUM. Someone introduces `idle_in_transaction_session_timeout`. The first `40001` shows up in logs and gets misdiagnosed as a database bug.

**Year three+:** Money is on the line. A write-skew bug ships, gets caught in a customer report, postmortem reveals snapshot isolation is not serializable. The team introduces `SELECT FOR UPDATE` in critical paths, retry wrappers around SERIALIZABLE transactions, and per-table autovacuum tuning. A separate replica appears for analytics. Eventually someone proposes moving the heaviest write workload to a distributed SQL store — and the cycle begins again, this time with HLC uncertainty intervals instead of XID wraparound.

The painful migration point is almost always the move from "one big OLTP database" to "OLTP + replica + analytics store." It is painful because every long query that was tolerable on the primary now needs an explicit decision about where it runs, and every transaction needs to know whether it's allowed to read stale data.

## Further reading

- [PostgreSQL Documentation — Routine Vacuuming](https://www.postgresql.org/docs/current/routine-vacuuming.html) — The canonical reference on freezing, anti-wraparound, and autovacuum knobs. Read it twice.
- [Cybertec — `idle_in_transaction_session_timeout` in PostgreSQL](https://www.cybertec-postgresql.com/en/idle_in_transaction_session_timeout-terminating-idle-transactions-in-postgresql/) — Short, opinionated, explains exactly the failure mode the parameter prevents.
- [Percona — InnoDB transaction history often hides dangerous debt](https://www.percona.com/blog/2014/10/17/innodb-transaction-history-often-hides-dangerous-debt/) — The reference post on history list length, written by people who have unstuck a lot of production MySQL.
- [Markus Winand — Serializable vs. Snapshot Isolation Level](https://use-the-index-luke.com/blog/2014-09/modern-sql-beyond-relational#mvcc) — Clearest plain-English explanation of write skew and why SSI exists.
- [Jeremy Cole — The basics of InnoDB undo logging and history system](https://blog.jcole.us/2014/04/16/the-basics-of-the-innodb-undo-logging-and-history-system/) — If you operate MySQL, this is the mental model you need.
- [AWS — Achieve high-speed InnoDB purge on RDS/Aurora MySQL](https://aws.amazon.com/blogs/database/achieve-a-high-speed-innodb-purge-on-amazon-rds-for-mysql-and-amazon-aurora-mysql/) — Practical tuning guide for `innodb_purge_threads` and related knobs at scale.
