# MVCC (Multi-Version Concurrency Control) — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

MVCC is a concurrency-control protocol in which every logical row is represented by an ordered chain of immutable physical versions, each stamped with the identity of the transaction that created it and (optionally) the one that obsoleted it. A reading transaction `T` does not acquire shared locks; instead it carries a **snapshot** — a description of which transactions were committed at the moment `T` began — and traverses each version chain picking the one version visible at that snapshot. Writers append new versions, optionally take row-level exclusive locks to serialise concurrent writes to the same logical row, and commit atomically by marking their transaction ID as committed in a central commit log (Postgres `clog`, InnoDB `trx_sys`, Oracle SCN-stamped transaction table). The protocol delivers **snapshot isolation (SI)** as its native isolation level; serializability is an opt-in layer on top.

### The core building blocks

- **Transaction ID (XID/trx_id/SCN).** A monotonically assigned identifier per write transaction. Postgres uses a 32-bit XID; InnoDB uses a 48-bit trx_id; Oracle uses a 6-byte System Change Number; CockroachDB/YugabyteDB use a 64-bit Hybrid Logical Clock timestamp.
- **Row version (tuple).** One physical copy of a row plus a header carrying the creator XID, the deleter XID (or 0/null), and engine-specific bits (hint bits, info bits, rollback pointer).
- **Version chain.** The linked sequence of versions of one logical row, traversed newest-to-oldest.
- **Snapshot.** A triple (`xmin_horizon`, `xmax`, `xip_list`): "every XID below `xmin_horizon` is settled, every XID at or above `xmax` is in the future, and the XIDs in `xip_list` were in flight at the moment of capture."
- **Visibility rule.** A boolean function `visible(version, snapshot)` that returns true iff the version's creator is committed and visible to the snapshot, and the version's deleter is not.
- **Garbage collector.** Background process that removes versions no live snapshot can still see — Postgres `VACUUM`/autovacuum, InnoDB purge thread, Oracle UNDO retention recycling.
- **Commit log.** The authoritative record of which XIDs committed vs aborted, consulted by the visibility check.

### How it relates to the broader landscape

MVCC is one of three families of concurrency control: **pessimistic locking** (strict two-phase locking, S2PL, used by DB2 and pre-2005 SQL Server), **optimistic concurrency control** (validate at commit, used by some in-memory engines like Hekaton), and **multi-version** (this one). MVCC is itself usually combined with row-level write locks, so it is not "lock-free" — it is "lock-free for readers." The closest cousin is **optimistic MVCC** as used in distributed systems (Spanner, CockroachDB), which adds a timestamp-ordering layer to make MVCC work across nodes without a central XID allocator.

## Where

### Where it runs / lives in the stack

Inside the storage engine of a transactional database, specifically in the **tuple access layer** that sits between the buffer manager and the executor. The visibility check is invoked on every tuple read by every plan node — it is one of the hottest code paths in the system. In Postgres the function is `HeapTupleSatisfiesMVCC` in `src/backend/access/heap/heapam_visibility.c`; in InnoDB it is `ReadView::changes_visible` over the clustered-index secondary version reconstructed from undo.

### Where you typically encounter it

- **PostgreSQL** (all versions; the only concurrency model it offers).
- **MySQL/InnoDB** (default since 5.5; the MyISAM engine had no MVCC).
- **Oracle Database** (since v3, the original commercial MVCC implementation).
- **SQL Server** under `READ_COMMITTED_SNAPSHOT` or `ALLOW_SNAPSHOT_ISOLATION` (off by default; lock-based otherwise).
- **MongoDB** via the WiredTiger storage engine (default since 3.2).
- **CockroachDB, YugabyteDB, Google Spanner, TiDB, FoundationDB** — distributed systems where MVCC is the substrate that makes consistent reads across shards possible.

### Ecosystem and tooling

- **For diagnosing bloat (Postgres):** `pgstattuple`, `pg_visibility`, the `pg_stat_user_tables` view, `pg_repack` and `pg_squeeze` for online table rebuilds.
- **For undo monitoring (InnoDB):** `INFORMATION_SCHEMA.INNODB_TRX`, `innodb_undo_tablespaces`, the `History list length` metric in `SHOW ENGINE INNODB STATUS`.
- **For Oracle:** `V$UNDOSTAT`, `DBA_HIST_UNDOSTAT`, `UNDO_RETENTION` parameter.
- **For distributed MVCC:** CockroachDB's `crdb_internal.kv_inflight_trace_spans`, YugabyteDB's `yb-admin` with HLC inspection.

## When

### When the topic emerged and why

The original proposal is Reed's 1978 MIT PhD thesis *"Naming and Synchronization in a Decentralized Computer System."* The first commercial implementation was Oracle in 1984. The pre-MVCC world was strict 2PL: every read took a shared lock, every write an exclusive lock, and a long analytical query could block the entire OLTP workload behind it. MVCC was specifically designed to break that contention. The big modern milestones are InnoDB shipping MVCC as the MySQL default (2010, 5.5), Postgres adding Serializable Snapshot Isolation (2011, 9.1), and the wave of distributed SQL systems (2014 onward) generalising MVCC over Hybrid Logical Clocks.

### When to use it in a project

Reach for it when:
- Reads and writes touch the same hot rows and you cannot tolerate readers waiting.
- You need point-in-time consistent reads (reporting, backups, change-data-capture) against a live OLTP store.
- Multiple application threads share a connection pool and you want predictable per-transaction views.
- You are picking a database — at this point MVCC is the default and the question is *which flavour*, not whether.

### When NOT to use it

Avoid it (or tune it down) when:
- You run very long transactions on a high-write table — they pin old versions and starve VACUUM/purge, ballooning storage and degrading scans (the classic Postgres "long idle-in-transaction" pathology).
- You expect snapshot isolation to be serializable — it is not. Workloads with write-skew-prone invariants (e.g. doctor on-call rosters, bank-account constraints across rows) need SSI or `SELECT ... FOR UPDATE`.
- You have a single-writer workload with no concurrency (SQLite WAL mode is fine; full MVCC is overkill).
- You are doing bulk UPDATEs on most of a Postgres table — append-in-place MVCC produces table bloat equal to the update size and forces a VACUUM that may itself be heavy.

## How

### How it works under the hood

There are two dominant implementation families. Both produce the same external behaviour; the storage representation differs.

**Family 1: append-new-version-in-place (Postgres).** Each heap tuple carries an `t_xmin` (creator XID) and `t_xmax` (deleter XID, 0 if live) in its 23-byte header, plus hint bits.

1. `BEGIN` allocates a virtual XID; a real XID is assigned lazily on first write.
2. First `SELECT` takes a snapshot: `(xmin = oldest running XID, xmax = next-to-be-assigned XID, xip = active XIDs)`.
3. `UPDATE` writes a new tuple on the same or another page with `t_xmin = my_xid`, and sets the old tuple's `t_xmax = my_xid`. The old tuple's `ctid` points to the new one, forming the version chain. Indexes normally get a new entry too — unless the **HOT** optimisation fires (no indexed column changed, room on the same page), in which case the index is left alone and reads follow the chain via the heap.
4. `COMMIT` writes `my_xid -> committed` to `clog`. No tuples are touched.
5. A later read sees the old tuple, asks the visibility function: "is `t_xmin` committed and below my snapshot's `xmax` and not in my `xip` list? Is `t_xmax` either zero, aborted, or above my snapshot?" — if both pass, the version is visible.
6. **VACUUM** later scans for tuples whose `t_xmax` is committed and below the system-wide `xmin horizon` (no live snapshot can see them) and reclaims their space. **Autovacuum** triggers based on `autovacuum_vacuum_scale_factor` (default 0.2 — 20% of the table dead).
7. **Freezing**: because XIDs are 32-bit, they wrap at ~4.3B. Before half the space is consumed, VACUUM rewrites old tuples' `t_xmin` to a special `FrozenTransactionId` (since 9.4, via a hint bit) so they remain visible forever. `autovacuum_freeze_max_age` defaults to 200 million; failure to freeze in time causes Postgres to refuse new XIDs and forces a single-user-mode recovery.

**Family 2: current-version-in-place with undo chain (InnoDB, Oracle).** The clustered index always holds the current version. Older versions are reconstructed on demand.

1. `UPDATE` modifies the row in place but first writes a **before-image undo record** to the rollback segment, and sets the row's `DB_ROLL_PTR` to point at it.
2. A reader builds a **ReadView** (InnoDB's snapshot: `up_limit_id`, `low_limit_id`, list of active trx_ids). Under `REPEATABLE READ` the ReadView is created on the first read and reused; under `READ COMMITTED` a new one is built per statement.
3. On reading the in-place row, if its `DB_TRX_ID` is invisible to the ReadView, InnoDB walks the `DB_ROLL_PTR` chain into the undo log, applying undo records in reverse until it finds a visible version. This is more CPU per read than Postgres, but updates do not move the row, so indexes pointing at the row's PK stay valid.
4. **Purge** is a background thread (one coordinator + N workers, `innodb_purge_threads`, default 4 since 5.7) that drops update-undo records once no active ReadView still needs them. The "history list length" is the depth of un-purged records — a high value (> 1M) is the canonical sign of a stuck long transaction.
5. Oracle uses essentially the same model with UNDO tablespaces and the `UNDO_RETENTION` parameter; running past retention produces the famous `ORA-01555 snapshot too old`.

**Isolation levels under MVCC:**

| Level | Postgres | InnoDB |
|-------|----------|--------|
| READ UNCOMMITTED | Treated as READ COMMITTED | Reads latest committed (no dirty reads) |
| READ COMMITTED | New snapshot per statement | New ReadView per statement |
| REPEATABLE READ | Single snapshot per transaction, no phantoms (true SI) | Single ReadView per transaction; gap locks added on locking reads to block phantoms |
| SERIALIZABLE | SI + predicate-locking SSI on top | Falls back to S2PL (degenerates to locking) |

Note: Postgres `REPEATABLE READ` already forbids phantom reads because the snapshot is genuinely consistent — it is stronger than the SQL-92 definition.

**Serializable Snapshot Isolation (SSI).** Postgres 9.1+ implements Cahill, Röhm & Fekete's 2008 algorithm. The engine tracks **rw-antidependencies** between concurrent SI transactions using `SIReadLock` predicate locks (held only by serializable transactions, never blocking writes). When a "dangerous structure" — two consecutive rw-edges in the conflict graph — is detected, one transaction is aborted with `40001 serialization_failure`. The application must retry. SSI eliminates write skew while preserving the read-doesn't-block-write property; the cost is false-positive abort rate on contended predicates.

### Key trade-offs

| Design choice | Gain | Cost |
|---|---|---|
| Append-in-place (Postgres) | Updates are O(1) writes, no undo lookup on read | Bloat; index amplification; VACUUM I/O |
| Undo-chain (InnoDB/Oracle) | Stable row location, indexes don't churn | Per-read undo traversal; rollback segment contention |
| SI as default | Reads never block; intuitive snapshot semantics | Write skew is possible |
| 32-bit XID (Postgres) | Compact tuple header | Wraparound machinery and the 200M-XID freeze cliff |
| 64-bit HLC (CockroachDB) | Globally comparable across nodes, no central XID allocator | Requires NTP; "uncertainty intervals" cause read restarts |
| SSI on top of SI | True serializability with non-blocking reads | False-positive aborts under contention |

### Common failure modes

- **Postgres table bloat under wide UPDATEs.** Updating every row of a 100 GB table writes 100 GB of new tuples and marks 100 GB dead. Cause: append-in-place semantics.
- **`idle in transaction` holding xmin horizon.** A forgotten `BEGIN` in an app pool pins `xmin`, so VACUUM cannot reclaim anything system-wide. Cause: visibility horizon = oldest live snapshot.
- **InnoDB history list explosion.** Same pattern: a long-running `REPEATABLE READ` transaction blocks purge; undo tablespace grows without bound.
- **`ORA-01555 snapshot too old`.** A long query needs an undo record that has been recycled under `UNDO_RETENTION` pressure. Cause: undo is finite, MVCC is not.
- **XID wraparound emergency shutdown (Postgres).** Autovacuum is disabled or stuck; `datfrozenxid` approaches 2 billion; the database refuses writes. Cause: freezing did not keep up.
- **SSI serialization-failure storms.** A heavily contended predicate (e.g. counting rows where `status='pending'`) produces a high rate of `40001` aborts under serializable. Cause: false positives in dangerous-structure detection.
- **Write skew under SI.** Two transactions read a shared invariant, each verifies it, each updates a different row, both commit — invariant now violated. Cause: SI is not serializable.

## Why

### Why it exists

The fundamental problem MVCC solves is the **read-write contention tax** of single-version stores. In a single-version world, a consistent read either takes shared locks (blocks writers, queues behind writers) or sees torn state. Neither is acceptable for an OLTP database that must also serve reporting queries. MVCC decouples the read path from the write path by trading storage (multiple versions) for concurrency. This is the central time–space–concurrency trade in database design.

### Why it looks the way it does

The alternative is **timestamp-ordered single-version** (Thomas Write Rule + restart on out-of-order access). This was tried — System R prototyped it — and lost because of cascading aborts and starvation under skewed workloads. Multi-versioning sidesteps both: an out-of-order read can simply read the older version, no restart needed. The choice between append-in-place (Postgres) and undo-chain (InnoDB/Oracle) is not arbitrary either: Postgres assumed cheap sequential writes and aggressive background cleanup; InnoDB assumed clustered-index physical organisation where moving rows is expensive because it invalidates secondary-index pointers. Both are coherent, and neither is universally better — they reflect different priors about workload shape.

The other non-obvious choice is **SI rather than serializability as the default**. SI gives you 95% of the intuitive correctness with 0% of the lock overhead. True serializability was the original goal of the SQL standard but turned out to be too expensive in practice. SSI is the modern compromise: serializability with the *read* path of SI intact, paid for in commit-time aborts rather than lock waits.

### Why it matters now

In 2026 every default-choice OLTP database — open source, commercial, single-node, distributed — runs MVCC. The growth area is **distributed MVCC**: CockroachDB, YugabyteDB, Spanner, TiDB, and the new wave of serverless Postgres-compatible stores (Neon, Aurora DSQL) all extend MVCC over HLC or TrueTime to deliver consistent snapshots across regions. Understanding the single-node fundamentals is the prerequisite for reasoning about them — every distributed-transaction failure-mode story eventually reduces to "whose snapshot saw whose version, and when did the GC run."

## Open questions / things to verify in practice

- Measure actual Postgres bloat after a 50% UPDATE on a 10M-row table; observe how `pg_stat_user_tables.n_dead_tup` climbs and when autovacuum fires.
- Open a `BEGIN; SELECT 1;` in one session, run heavy DML in another, watch `txid_current()` vs `pg_stat_activity.backend_xmin` to see horizon pinning in action.
- Construct a write-skew scenario (e.g. two doctors both going off-call) under `REPEATABLE READ` to confirm SI lets it through, then re-run under `SERIALIZABLE` and observe the `40001` abort.
- On InnoDB, run a long `REPEATABLE READ` transaction and watch `History list length` in `SHOW ENGINE INNODB STATUS` grow.
- Compare cost of a 1000-version chain read in InnoDB (undo traversal) vs Postgres (HOT-chain walk) using `EXPLAIN ANALYZE` timings.
- On CockroachDB, force an uncertainty-interval restart by writing on one node and reading on another with skewed clocks; observe the retry.

Sources consulted:
- [PostgreSQL Documentation — Transaction Isolation](https://www.postgresql.org/docs/current/transaction-iso.html)
- [PostgreSQL Documentation — Routine Vacuuming](https://www.postgresql.org/docs/current/routine-vacuuming.html)
- [PostgreSQL Documentation — Heap-Only Tuples (HOT)](https://www.postgresql.org/docs/current/storage-hot.html)
- [PostgreSQL Wiki — SSI](https://wiki.postgresql.org/wiki/SSI)
- [Cahill et al. — Serializable Snapshot Isolation in PostgreSQL (VLDB 2012)](https://www.drkp.net/papers/ssi-vldb12.pdf)
- [MySQL 8.4 Reference Manual — InnoDB Multi-Versioning](https://dev.mysql.com/doc/refman/8.4/en/innodb-multi-versioning.html)
- [Jeremy Cole — The basics of the InnoDB undo logging and history system](https://blog.jcole.us/2014/04/16/the-basics-of-the-innodb-undo-logging-and-history-system/)
- [Postgres Professional — MVCC in PostgreSQL: Snapshots](https://postgrespro.com/blog/pgsql/5967899)
- [InterDB — Heap Only Tuple (HOT)](https://www.interdb.jp/pg/pgsql07/01.html)
- [CockroachDB Paper — The Resilient Geo-Distributed SQL Database](https://rcs.uwaterloo.ca/~ali/cs854-f23/papers/cockroachdb.pdf)
- [YugabyteDB Docs — Fundamentals of Distributed Transactions](https://docs.yugabyte.com/preview/architecture/transactions/transactions-overview/)
