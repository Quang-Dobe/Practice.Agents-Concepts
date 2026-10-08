# Transaction Isolation Levels — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

An isolation level is a contract between a DBMS and a client transaction that states which **anomalies** (non-serializable interleavings) the engine may expose to that transaction. The reference point is **serializability**: an execution of concurrent transactions is serializable if its result equals the result of some serial (one-at-a-time) order of those same transactions. Each weaker level permits a defined set of departures from serializability in exchange for less blocking or fewer aborts.

ANSI SQL-92 defines four levels by the phenomena they forbid. That definition turned out to be ambiguous and tied to lock-based implementations, so the working definitions today come from two later sources: [Berenson et al., "A Critique of ANSI SQL Isolation Levels" (SIGMOD 1995)](https://www.microsoft.com/en-us/research/publication/a-critique-of-ansi-sql-isolation-levels/), which added Snapshot Isolation and new phenomena, and Atul Adya's 1999 MIT thesis, which defines anomalies as cycles in a dependency graph (G0, G1, G2-item, G2) independent of how the engine is built.

### The core building blocks

- **Phenomena (anomalies).** The vocabulary from Berenson et al.:
  - P0 dirty write: overwriting another transaction's uncommitted write.
  - P1 dirty read.
  - P2 non-repeatable read.
  - P3 phantom: a predicate read returns a different row set on re-execution.
  - P4 lost update: a read-modify-write is overwritten by a concurrent one.
  - A5A read skew: you see a mix of pre- and post-commit state across two related items.
  - A5B write skew: two transactions read overlapping data, write disjoint rows, and break an invariant together.
- **Dependency graph.** Nodes are transactions. Edges are `ww` (write-write), `wr` (write-read) and `rw` (read-write, also called an anti-dependency). An execution is serializable when this graph has no cycle. Every anomaly is a specific cycle shape.
- **Locks.** Shared (S) and exclusive (X) locks on rows, plus **predicate / range locks** that cover rows that do not exist yet (SQL Server key-range locks, InnoDB next-key and gap locks). Lock duration (statement vs. transaction) is what separates the lock-based levels.
- **Snapshots.** A read view of the database as of a point in time, built from multiple row versions (MVCC, which has its own topic in this repo). The snapshot's scope, per statement or per transaction, is what separates the snapshot-based levels.
- **Write-conflict rule.** Under Snapshot Isolation, two concurrent transactions that write the same row cannot both commit. The usual rule is *first-updater-wins*: the second writer waits, and if the first one commits, the second aborts.
- **Serialization failure.** The abort the engine raises to keep its guarantee. SQLSTATE `40001` in PostgreSQL and CockroachDB, error `3960` in SQL Server SNAPSHOT, `ORA-08177` in Oracle.

### How it relates to the broader landscape

Isolation is the "I" in ACID and belongs to the concurrency-control family, next to locking protocols (two-phase locking, 2PL), MVCC, and optimistic concurrency control (OCC). It is a different axis from **consistency models** in distributed systems (linearizability, causal, eventual), which describe what replicas show to readers. The two meet in **strict serializability** (serializable plus real-time order), which Spanner advertises as "external consistency".

## Where

### Where it runs / lives in the stack

Isolation is enforced inside the database engine by the transaction manager and lock manager (or the version-visibility check). The client chooses the level: per transaction (`SET TRANSACTION ISOLATION LEVEL ...` or `BEGIN ISOLATION LEVEL ...`), per session, or as a server or database default. The application layer only *selects* the level and *handles* the failures the level produces. It cannot add isolation the engine does not provide.

### Where you typically encounter it

The ANSI names are portable. The behavior behind them is not:

| Engine (version checked) | Default | Read Uncommitted | Read Committed | Repeatable Read | Serializable |
|---|---|---|---|---|---|
| PostgreSQL 18 | RC | Behaves as RC | Snapshot per statement | Snapshot Isolation; phantoms also prevented | SSI (since 9.1) |
| MySQL 8.4 InnoDB | RR | Real dirty reads | Snapshot per statement, no gap locks | Snapshot for plain `SELECT`; locking reads and writes see latest committed rows under next-key locks; **lost update allowed** | Plain `SELECT` becomes `SELECT ... FOR SHARE` when autocommit is off |
| SQL Server 2022/2025 | RC, **lock-based** (`READ_COMMITTED_SNAPSHOT` OFF) | Same as `NOLOCK` | Locking, or snapshot per statement with RCSI | S locks held to commit; phantoms possible | Key-range locks held to commit |
| Azure SQL Database | RC with RCSI **ON** | Same | Snapshot per statement | Same as SQL Server | Same |
| Oracle | RC | Not offered | Snapshot per statement | Not offered | Actually Snapshot Isolation (`ORA-08177`) |
| CockroachDB | Serializable | Upgraded to RC (or Serializable if RC is disabled) | GA in 24.1, gated by `sql.txn.read_committed_isolation.enabled` | Upgraded to Serializable | Serializable |

Sources: [PostgreSQL 18 docs](https://www.postgresql.org/docs/current/transaction-iso.html), [MySQL 8.4 docs](https://dev.mysql.com/doc/refman/8.4/en/innodb-transaction-isolation-levels.html), [SQL Server docs](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql), [CockroachDB docs](https://www.cockroachlabs.com/docs/stable/read-committed). SQL Server's `SNAPSHOT` level is a fifth option. It requires `ALLOW_SNAPSHOT_ISOLATION ON` and is true Snapshot Isolation.

The MySQL Repeatable Read row comes from [Kleppmann's Hermitage tests](https://github.com/ept/hermitage) and [Jepsen's 2023 analysis of MySQL 8.0.34](https://jepsen.io/analyses/mysql-8.0.34). Both found that it allows lost update and G2-item, and Jepsen found it also violates Monotonic Atomic View. MariaDB 11.8 turned on `innodb_snapshot_isolation` by default so that Repeatable Read raises error 1020 on write conflicts instead of losing the update.

### Ecosystem and tooling

- **For setting the level from code.** JDBC `Connection.setTransactionIsolation`, .NET `System.Data.IsolationLevel` (used by ADO.NET and EF Core `BeginTransaction`), Spring `@Transactional(isolation = ...)`, SQLAlchemy `isolation_level`, and the Django `OPTIONS["isolation_level"]` setting for PostgreSQL.
- **For observing locks and versions.**
  - PostgreSQL: `pg_locks`. Predicate locks show up as `SIReadLock`.
  - SQL Server: `sys.dm_tran_locks` and `sys.dm_tran_version_store_space_usage`.
  - MySQL: `performance_schema.data_locks`, and `SHOW ENGINE INNODB STATUS` for history list length.
- **For testing what an engine actually does.** [Hermitage](https://github.com/ept/hermitage) runs hand-written interleavings per engine. [Jepsen's Elle](https://github.com/jepsen-io/elle) checks transaction histories for dependency cycles.
- **Tuning knobs.**
  - PostgreSQL: `max_pred_locks_per_transaction` and `default_transaction_isolation`.
  - MySQL: `transaction_isolation`.
  - SQL Server: `READ_COMMITTED_SNAPSHOT` and `ALLOW_SNAPSHOT_ISOLATION`.

## When

### When the topic emerged and why

- **1976.** Gray et al. defined "degrees of consistency" 0–3 for lock-based systems. The degrees differed in which locks a transaction took and whether it held them until commit.
- **SQL-92.** The standard renamed the degrees as the four levels and defined them through phenomena P1–P3, so that vendors could describe them without naming locks. The standard default is SERIALIZABLE.
- **Snapshot engines.** Oracle had already shipped multiversion reads. Its "serializable" passed the ANSI phenomenon tests yet still allowed write skew. This gap motivated the 1995 Berenson critique, which formally defined Snapshot Isolation.
- **2005.** SQL Server 2005 added `SNAPSHOT` and RCSI to reduce reader/writer blocking.
- **2008–2011.** Cahill, Röhm and Fekete published Serializable Snapshot Isolation (SIGMOD 2008). [PostgreSQL 9.1 shipped it in 2011](https://arxiv.org/abs/1208.4179), the first production implementation.
- **Since then.** Distributed SQL systems went the other way. They launched as serializable-only and later added weaker levels: CockroachDB Read Committed in 23.2/24.1, and Repeatable Read (Snapshot Isolation) in Spanner.

### When to use it in a project

These are reasons to choose a non-default level deliberately:

- **Serializable.** Reach for it when a correctness invariant spans rows or predicates ("at least one doctor on call", "sum of allocations ≤ budget", "no overlapping bookings"), the transaction is short, and you can wrap it in a retry loop.
- **Repeatable Read or Snapshot.** Reach for it when one unit of work runs several reads that must agree: reports, exports, balance checks across tables, consistent backups.
- **Read Committed with explicit locks.** Reach for it when contention is high on a few hot rows. `SELECT ... FOR UPDATE`, conditional `UPDATE ... WHERE version = ?`, or a unique constraint turns the race into a write-write conflict that every level handles.
- **RCSI.** On SQL Server, reach for it when readers block writers under the lock-based default and you want PostgreSQL-like Read Committed.

### When NOT to use it

- **Serializable everywhere.** Avoid it when the code has no retry path, transactions are long, or hot rows collide constantly. Abort rates climb, and under lock-based Serializable (SQL Server, MySQL) deadlocks and blocking climb too.
- **Read Uncommitted for speed.** Avoid it. With MVCC, readers do not block writers at Read Committed anyway. In SQL Server, `NOLOCK` can return rows twice or skip them during page splits, not only uncommitted ones.
- **Repeatable Read for "no lost updates" on MySQL.** InnoDB Repeatable Read does not detect write-write conflicts. Use `FOR UPDATE` or atomic `UPDATE`.
- **Raising the level for a single-row race.** A unique index or an atomic `UPDATE ... SET qty = qty - 1 WHERE qty > 0` is cheaper and holds at every level.

## How

### How it works under the hood

Engines use one of three mechanisms. The SQL names map onto them differently in each engine.

**A. Lock-based (two-phase locking).** SQL Server's default levels and InnoDB's locking reads work this way.

1. Every write takes an X lock held until commit. This prevents P0 at every level.
2. The level decides how reads lock:
   - Read Uncommitted takes no S lock.
   - Read Committed takes an S lock and releases it right after the read.
   - Repeatable Read holds S locks until commit.
   - Serializable also holds **range locks** on the scanned key interval, so concurrent inserts into that interval block. This is how phantoms are prevented.
3. Conflicts show up as blocking. Cycles in the wait-for graph are resolved by the deadlock detector killing a victim.

**B. Snapshot-based (MVCC).** Used by PostgreSQL, Oracle, InnoDB plain reads, SQL Server RCSI and SNAPSHOT.

1. At the first statement (Read Committed: every statement), the engine records a snapshot. In PostgreSQL that is xmin, xmax and the list of in-progress transaction IDs. In InnoDB it is the "read view".
2. Each row version is visible only if its creator committed before the snapshot. Readers never block on writers.
3. On `UPDATE` of a row that a concurrent transaction changed:
   - Read Committed in PostgreSQL waits, then re-evaluates the `WHERE` clause against the newest version and proceeds.
   - Repeatable Read in PostgreSQL, SQL Server SNAPSHOT and Oracle Serializable apply first-updater-wins. The transaction waits. If the other transaction committed, it aborts (`could not serialize access due to concurrent update`). If the other transaction rolled back, it continues.
   - InnoDB Repeatable Read updates the latest committed version without aborting. That is the lost-update hole.
4. Snapshot Isolation checks only write-write conflicts. A write skew that touches disjoint rows passes.

**C. Serializable Snapshot Isolation (SSI).** PostgreSQL Serializable works this way.

1. Run the transaction under Snapshot Isolation as in B.
2. Record every read as a non-blocking **SIREAD lock**. It starts at tuple granularity and is promoted to page or relation when memory runs short.
3. When transaction T1 reads something that a concurrent T2 writes, record an `rw` edge T1 → T2.
4. Cahill's theorem: every non-serializable SI execution contains a **dangerous structure** of two consecutive `rw` edges T_in → T_pivot → T_out, where T_out commits first. Abort one participant when the structure appears, typically at commit time.
5. The check is conservative. It does not search for full cycles, so some aborts are false positives. Coarser SIREAD locks add more of them.

The overview's on-call doctors under SI vs. SSI:

```
T1 (Alice)                          T2 (Bob)
SELECT on_call rows -> Alice, Bob   SELECT on_call rows -> Alice, Bob
UPDATE Alice SET on_call=false      UPDATE Bob SET on_call=false
COMMIT                              COMMIT
SI : disjoint rows, no ww conflict -> both commit, nobody on call
SSI: T1 read Bob (T2 wrote)   => T1 -rw-> T2
     T2 read Alice (T1 wrote) => T2 -rw-> T1  => cycle, second committer gets 40001
```

### Key trade-offs

| Design choice | You gain | You give up |
|---|---|---|
| Lock-based isolation (2PL) | No version storage. Conflicts wait instead of aborting. Serializable is straightforward | Readers and writers block each other. Deadlocks. Throughput collapses on long reads |
| Snapshot-based isolation (MVCC) | Readers never block writers. Consistent multi-statement reads | Version storage (PostgreSQL heap bloat, InnoDB undo, SQL Server version store in tempdb or the ADR persistent version store). Write skew allowed |
| SSI vs. lock-based Serializable | [Close to SI performance; much faster than 2PL on read-heavy loads](https://arxiv.org/abs/1208.4179). No read blocking | False-positive aborts. Every caller needs retry logic. Memory for predicate locks |
| Statement snapshot (RC) vs. transaction snapshot (RR) | Fewer conflicts. Short version retention | Different statements in one transaction disagree. Read skew possible |
| Abort on conflict (SI/SSI) vs. silently take the latest version (InnoDB RR) | Lost updates impossible | Application must retry. Higher abort rate on hot rows |
| Higher default (CockroachDB Serializable) vs. lower default (PostgreSQL RC) | Correct by default | Applications ported from RC engines meet retry errors they never handled |

### Common failure modes

- **Serializable in production returns 500s under load.** Cause: `40001` / `3960` / `ORA-08177` raised with no retry wrapper. The retry also has to re-run the reads, not only the final write.
- **Account balance loses decrements on MySQL.** Cause: app-side read-modify-write under InnoDB Repeatable Read, which does not detect lost updates.
- **Write skew in "Serializable" Oracle code.** Cause: Oracle Serializable is Snapshot Isolation. Cross-row invariants need `SELECT ... FOR UPDATE` or a constraint.
- **SSI guarantee silently void.** Cause: one writer in the conflict runs at Read Committed. SSI covers only transactions that all run at Serializable.
- **Abort storm after a schema or data change.** Cause: SIREAD locks promoted to relation level because `max_pred_locks_per_transaction` is too low or a sequential scan replaced an index scan.
- **Disk or tempdb growth, slow queries hours later.** Cause: one long-open transaction pins the snapshot horizon. PostgreSQL vacuum cannot remove dead tuples, the InnoDB history list grows, the SQL Server version store fills.
- **Deadlocks on MySQL insert-if-absent.** Cause: Repeatable Read gap and next-key locks taken by `SELECT ... FOR UPDATE` on a missing key, followed by concurrent `INSERT`s into the same gap.
- **SQL Server report blocks the OLTP workload.** Cause: lock-based Read Committed default. Readers hold S locks while writers queue. RCSI is off unless you enable it.
- **Isolation level leaks across requests.** Cause: a session-level `SET` on a pooled connection persists for the next borrower.

## Why

### Why it exists

Concurrency raises throughput and lowers latency, but interleaved reads and writes can produce states no serial execution could reach. Full serializability has a cost: blocking under locks, or aborts under optimistic schemes. That cost depends on the workload, so a single fixed choice would be wrong for many applications. Isolation levels make the trade-off explicit and selectable per transaction. A nightly report can run at a snapshot without blocking checkout, while the payment transfer runs Serializable.

### Why it looks the way it does

The four ANSI names describe 1970s lock durations dressed up as phenomena. That is why they fit snapshot engines badly. Snapshot Isolation prevents every ANSI phenomenon in its strict interpretation and still is not serializable. Vendors kept the familiar names and mapped their own mechanisms onto them, which produced the engine table above.

The non-obvious design insight is SSI. The obvious way to make SI serializable is to bring back read locks or range locks, which turns it into 2PL and gives up non-blocking reads. A second alternative is static analysis of the application ("Making Snapshot Isolation Serializable", Fekete et al., 2005): find dangerous transaction pairs and add artificial write conflicts by hand. That approach breaks whenever the code changes. SSI accepts some false-positive aborts in exchange for keeping reads non-blocking and requiring no application analysis. Because the cost is paid only when transactions conflict, PostgreSQL could make it the meaning of `SERIALIZABLE`.

### Why it matters now

Isolation is under active change in 2025–2026:

- Distributed SQL systems that launched as serializable-only have added weaker levels for compatibility and latency. CockroachDB added Read Committed (GA in 24.1) and Spanner added Repeatable Read.
- [Aurora DSQL](https://aws.amazon.com/blogs/database/concurrency-control-in-amazon-aurora-dsql/) offers only strong Snapshot Isolation, so write skew is a design constraint you must handle in the schema.
- MariaDB 11.8 changed Repeatable Read semantics by default.
- SQL Server 2025 added *optimized locking* (TID locking, lock-after-qualification under RCSI).

Teams that move between Postgres-compatible clouds or migrate MySQL to PostgreSQL get the same level names with different guarantees, and the resulting bugs only show up under concurrency. The topic is stable in theory and changing in practice.

## Open questions / things to verify in practice

- Does my driver or ORM set an isolation level on connect, or inherit the server default? Check what `SHOW transaction_isolation` / `SELECT @@transaction_isolation` / `DBCC USEROPTIONS` reports on a pooled connection.
- Reproduce the doctors' write skew on PostgreSQL at Repeatable Read, then confirm Serializable aborts one transaction with `40001`.
- Reproduce a lost update on MySQL 8.4 Repeatable Read, and compare against PostgreSQL Repeatable Read, which should raise `could not serialize access`.
- Measure the abort rate of a Serializable hot-row workload, and check how it changes when the predicate uses an index vs. a sequential scan, since that changes SIREAD granularity.
- On SQL Server, compare blocking for the same report query with `READ_COMMITTED_SNAPSHOT` OFF vs. ON, and watch version-store growth while a long transaction stays open.
- Write a retry wrapper and confirm it re-executes the whole transaction body, reads included, with bounded backoff.
