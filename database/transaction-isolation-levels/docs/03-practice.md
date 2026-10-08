# Transaction Isolation Levels — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend you never choose an isolation level. The ORM opens transactions at the server default, usually Read Committed. Nobody thinks about it until a counter loses increments, a coupon is redeemed twice, or stock goes negative in a flash sale. Isolation becomes load-bearing wherever two requests do read-check-write on the same data.

In wallets and ledgers it is a security boundary. Attackers fire parallel requests at transfer endpoints to hit the race window on purpose. In booking and quota systems it guards invariants that span rows ("no overlapping reservations"), which is write-skew territory (see `02-deep-dive.md § How`).

On SQL Server it usually shows up first as blocking: a report holds shared locks while checkout queues behind it. On CockroachDB, which defaults to Serializable, it shows up as retry errors that ported code never handled.

## Best practices

### 1. Set the level per transaction, never per pooled session
**Do:** Pass the level when you open the transaction (`BEGIN ISOLATION LEVEL ...`, `BeginTransaction(IsolationLevel.X)`, `@Transactional(isolation = ...)`).
**Why:** On SQL Server, `sp_reset_connection` [does not reset the isolation level](https://learn.microsoft.com/en-us/archive/blogs/jimmymay/sp_reset_connection-does-not-reset-transaction-isolation-level-unexpected-behavior-by-design). One Serializable request leaves its pooled connection at Serializable for the next, unrelated borrower. The result is random blocking you cannot reproduce.
**Avoid:** Raw `SET TRANSACTION ISOLATION LEVEL` on a borrowed connection, or `new TransactionScope()` with no options, whose default is Serializable.

### 2. Fix single-row races with atomic SQL or constraints first
**Do:** Write `UPDATE accounts SET balance = balance - 50 WHERE id = 7 AND balance >= 50` and check rows affected. Use unique indexes for "only once" and PostgreSQL `EXCLUDE USING gist` for "no overlap".
**Why:** These hold at every level on every engine, so a later engine migration or default change cannot silently remove the protection.
**Avoid:** Raising the whole service to Serializable to fix one double-redeem bug.

### 3. Lock read-check-write explicitly, in a fixed order
**Do:** `SELECT ... FOR UPDATE` the rows you base a decision on, sorted by primary key when there are several. In an ORM, add a concurrency token (`rowversion`, `xmin`, or a version column).
**Why:** This turns a lost update into a write-write conflict that every engine handles. The consistent order removes the classic two-account transfer deadlock.
**Avoid:** Loading an entity, mutating it, and calling `SaveChanges()` with no token, which loses updates at RC and at MySQL Repeatable Read.

### 4. Ship one retry wrapper before anyone uses Repeatable Read or Serializable
**Do:** Write a single `RunInTransaction(level, fn)` helper. It catches serialization failures and deadlocks (PostgreSQL `40001`/`40P01`, MySQL `1213`, SQL Server `1205`/`3960`), rolls back, and re-runs the whole `fn`, reads included. It stops after 3–5 attempts with jittered backoff.
**Why:** Without it, every abort becomes a 500. With unbounded retries, aborts multiply load: one team logged [1.9M serialization errors on 1M requests](https://www.nirvanatech.com/blog/investigating-serialization-anomalies-in-postgresql) on day one.
**Avoid:** Retrying only the failed statement, because after `40001` the transaction is dead and its earlier reads are stale.

### 5. Keep side effects out of the retried body
**Do:** Call payment APIs, send email, and publish events after commit, or write them to an outbox table inside the transaction.
**Why:** The wrapper replays `fn`, so a card charge inside it runs once per attempt.
**Avoid:** HTTP calls inside the transaction "because the write depends on the result."

### 6. Keep transactions short and cap them server-side
**Do:** Set PostgreSQL `idle_in_transaction_session_timeout` (plus `transaction_timeout` on 17+) and `lock_timeout`. Lower InnoDB `innodb_lock_wait_timeout` from its 50 s default for OLTP. Alert on oldest-transaction age.
**Why:** One forgotten open transaction pins the snapshot horizon. Vacuum stalls, the InnoDB history list grows, the tempdb version store fills, and latency degrades hours later, far from the cause.
**Avoid:** Holding a transaction across user think time ("lock the record while the form is open").

### 7. Make Serializable predicates hit indexes
**Do:** Run `EXPLAIN` on every query inside a Serializable transaction. Raise `max_pred_locks_per_transaction` if `pg_locks` shows relation-level `SIReadLock`s.
**Why:** PostgreSQL takes SIREAD locks on what it scans. A sequential scan, which the planner picks readily on a 10-row table, locks the whole relation and makes every concurrent transaction a false-positive conflict.
**Avoid:** "Small table, no index needed" for tables read inside Serializable transactions.

### 8. Give reports a snapshot, not a higher OLTP level
**Do:** Run multi-query reports at Repeatable Read / `SNAPSHOT`, or PostgreSQL `SERIALIZABLE READ ONLY DEFERRABLE`, which waits for a safe snapshot and then cannot fail serialization. Use a replica when you have one.
**Why:** The report's numbers agree with each other, and it never blocks or aborts checkout.
**Avoid:** A report built from separate autocommit queries, where totals and line items come from different moments.

### 9. Enable SQL Server RCSI only after an audit
**Do:** Before `SET READ_COMMITTED_SNAPSHOT ON`, find code that relies on readers blocking (queue tables, check-then-insert procs, triggers that validate other rows) and add `UPDLOCK` or `READCOMMITTEDLOCK` there. Size tempdb, or the ADR version store.
**Why:** Code that was accidentally correct because a read waited for a writer now reads the old version and races.
**Avoid:** Flipping RCSI as a quick blocking fix.

### 10. Test races on the real engine with real concurrency
**Do:** In CI, open two connections to the production engine (Testcontainers) and force the interleaving with a barrier. For money endpoints, fire 50 parallel requests and assert the invariant.
**Why:** SQLite, H2, and the EF Core in-memory provider have different semantics or no transactions at all, so a pass there proves nothing.
**Avoid:** Trusting staging, which rarely reaches the concurrency that opens the window.

## Anti-patterns to recognize

- **`NOLOCK` as a performance hint**: `WITH (NOLOCK)` gets added to every SQL Server SELECT to stop blocking. It reads uncommitted data and can return rows twice or skip them during page splits. Enable RCSI instead.
- **Serializable as a global default with no retry path**: Someone sets `default_transaction_isolation = 'serializable'` to be safe. The first traffic spike turns `40001` into user-facing errors. Ship the retry wrapper first, then scope Serializable to transactions that guard cross-row invariants.
- **Mixed-level guard**: The API path runs Serializable, but a nightly batch writes the same tables at Read Committed. SSI only protects transactions that all run at Serializable, so the invariant breaks once a night. Enforce the level in the shared helper, not at call sites.
- **Trusting the name across engines**: Code that relied on PostgreSQL Repeatable Read aborting on a concurrent update moves to MySQL, where InnoDB silently takes the latest version and loses the update. Re-audit every read-check-write when you change engines.
- **Distributed lock instead of a database guarantee**: A Redis lock wraps "check balance, then debit." A GC pause or lease expiry lets two holders in, and the database never knew the rule existed. If the data lives in one database, use a row lock, conditional `UPDATE`, or constraint there.

## Real-world usage patterns

**Custodial wallet with internal transfers.** A small exchange lets users move balances between accounts, then withdraw. The [Flexcoin incident (2014)](https://hackingdistributed.com/2014/04/06/another-one-bites-the-dust-flexcoin) was a read-check-write race: thousands of simultaneous transfer requests overdrew accounts before balances updated, and 896 BTC left in one attack. The fix pattern is a conditional `UPDATE ... WHERE balance >= amount` plus a ledger row with a unique idempotency key. **Lesson:** an isolation bug in a money path is a security vulnerability. Rate limiting does not close it, because a handful of concurrent requests inside the window is enough.

**Resource allocation on PostgreSQL Serializable.** An insurance-tech team guarded allocations across two tables with Serializable and expected rare aborts. They got [retry storms, with about 1,000 requests exhausting 30 retries](https://www.nirvanatech.com/blog/investigating-serialization-anomalies-in-postgresql), caused by sequential scans on a roughly 10-row table that promoted SIREAD locks to the relation. They moved to Repeatable Read, because their transactions read and wrote the *same* rows, so first-updater-wins was enough. **Lesson:** Serializable only earns its cost when reads and writes touch *different* rows. Name the anomaly before picking the level.

**Illustrative scenario: room booking.** A clinic scheduler must never double-book a room. Serializable works, but at peak it aborts bookings for unrelated rooms. A PostgreSQL exclusion constraint on `(room_id WITH =, slot WITH &&)` enforces the rule at Read Committed, and conflicts surface as `23P01` ("slot taken"). **Lesson:** a constraint is cheaper, holds at every level, and survives code changes.

**Illustrative composite: SQL Server ERP with daytime reporting.** Finance dashboards held shared locks, and checkout latency spiked every morning. Enabling RCSI fixed the blocking. Later a job-queue table processed some jobs twice, because "SELECT TOP 1 pending, then UPDATE" had relied on the read blocking. **Lesson:** RCSI changes semantics, not only performance. Queue reads need `UPDLOCK, READPAST` or an atomic `UPDATE ... OUTPUT`.

**Two camps exist on the default.** The CockroachDB / FoundationDB school argues for Serializable everywhere plus mandatory retries, so correctness never depends on spotting write skew. Most PostgreSQL and MySQL shops keep Read Committed with targeted `FOR UPDATE`, conditional updates, and constraints. The tie-breaker is whether you already have one enforced transaction helper with retries.

## Operational checklist

- **Effective level:** On a *pooled* connection mid-request, does `SHOW transaction_isolation` / `SELECT @@transaction_isolation` / `DBCC USEROPTIONS` report the level you think you run at?
- **Retry coverage:** Is there exactly one transaction helper, does it re-run the whole body on the engine's serialization and deadlock codes, and does a test force a `40001` and assert success on attempt 2?
- **Metrics:** Are serialization failures, deadlocks (`pg_stat_database.deadlocks`, SQL Server error 1205), retries per transaction, and lock-wait time graphed and alerted on?
- **Long transactions:** Is there an alert on oldest-transaction age (`pg_stat_activity.xact_start`, InnoDB history list length, `sys.dm_tran_active_snapshot_database_transactions`) and a server-side timeout for idle-in-transaction sessions?
- **Money, inventory, coupons:** Has every read-check-write been reviewed for atomic SQL, `FOR UPDATE`, or a constraint, and hit by a parallel-request test?
- **Side effects:** Are external calls outside the retried body or routed through an outbox?
- **Cost:** Are retries bounded, and is version storage (PostgreSQL bloat, InnoDB undo, tempdb version store) sized and monitored?
- **Serializable hygiene:** Do all writers to Serializable-guarded tables also run at Serializable, with indexed predicates?
- **Onboarding:** Is there a one-paragraph team rule ("default RC; `FOR UPDATE` for X; Serializable via the helper for Y") a new engineer reads on day one?

## How this topic typically evolves in a codebase

Projects start on the engine default with ORM-managed transactions and no stated policy. The first concurrency bug, usually a lost increment or a duplicate redemption, gets patched locally with a `FOR UPDATE` or a unique index. Six months in, the codebase has scattered `SET TRANSACTION` calls, several hand-rolled retry loops catching different error codes, and nobody can say what level a given endpoint runs at.

Mature teams converge on one transaction helper that owns the level, the retry policy, and the timeout. The default stays Read Committed, a short named list of invariant operations runs Serializable, reports move to snapshots or replicas, and invariants that fit a constraint move into the schema.

The painful migration point is any change to the *default*: raising to Serializable, enabling RCSI, or switching engines. Every transaction's semantics change at once, and the bugs only appear under production concurrency. Teams with the central helper change one function and watch one retry metric. Teams without it audit every call site under deadline pressure.

## Further reading

- [PostgreSQL docs: Serialization Failure Handling](https://www.postgresql.org/docs/current/mvcc-serialization-failure-handling.html). Which SQLSTATEs to retry and why the whole transaction must re-run; short and exact.
- Martin Kleppmann, *Designing Data-Intensive Applications*, chapter 7 "Transactions". The best practitioner treatment of lost update and write skew in real application code.
- [Hermitage](https://github.com/ept/hermitage). Two-session scripts showing what each engine's level names actually do; run them against your engine version.
- [Warszawski & Bailis, "ACIDRain" (SIGMOD 2017), summarized by Adrian Colyer](https://blog.acolyer.org/2017/08/07/acidrain-concurrency-related-attacks-on-database-backed-web-applications/). 22 exploitable isolation bugs across 12 e-commerce apps; reframes isolation as an attack surface.
- [Nirvana: Investigating serialization anomalies in PostgreSQL](https://www.nirvanatech.com/blog/investigating-serialization-anomalies-in-postgresql). A rare production SSI post-mortem with real numbers.
