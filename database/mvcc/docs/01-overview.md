# MVCC (Multi-Version Concurrency Control) — Overview

> MVCC lets a database serve many concurrent transactions without blocking readers behind writers, by keeping multiple historical versions of each row and showing each transaction the snapshot that was "true" the moment it started.

## The 30-second version

MVCC is the technique modern databases use to answer the question: "what should a transaction see when other transactions are editing the same rows at the same time?" Instead of locking rows and forcing readers to wait for writers (and vice versa), the database keeps the old version of a row around when someone updates it. Each transaction reads from a consistent snapshot of the database, frozen at the moment it began. The result is high concurrency without sacrificing correctness — and it is the default in Postgres, MySQL/InnoDB, Oracle, SQL Server (under RCSI/Snapshot), and MongoDB's WiredTiger engine.

## The mental model

Think of a database as a shared Google Doc with **version history always on**.

Alice opens the doc at 9:00. While she is reading paragraph 3, Bob edits paragraph 3 and saves at 9:01. In a "lock-based" world, Alice would see Bob's cursor on her paragraph and have to wait — or worse, see half-written text. In the Google Doc world, Alice keeps seeing the 9:00 version of paragraph 3 until she explicitly refreshes. Bob's edit becomes a *new revision* sitting alongside the old one. Anyone who opens the doc at 9:02 sees Bob's version. Anyone who opened it at 8:59 still sees the old one.

That is MVCC. The database does not overwrite a row in place — it writes a **new version** of the row, tagged with the transaction that created it. Every transaction carries a snapshot ID, and when it reads a row it walks the version chain and picks the version that was visible at its snapshot. Readers never block writers. Writers never block readers. The only thing two transactions actually fight over is writing the *same row* — and that conflict gets handled separately.

## What it is NOT

- **Not the same as locking.** Pessimistic 2PL (two-phase locking) makes readers and writers queue up. MVCC sidesteps that for the read path entirely.
- **Not "no locks ever."** Writers still take row-level locks to prevent two transactions from creating conflicting new versions of the same row.
- **Not free.** Old versions pile up and have to be garbage-collected — Postgres calls this `VACUUM`, InnoDB calls it purge.
- **Not the same as serializable isolation.** MVCC gives you *snapshot* isolation by default, which is strong but allows a subtle anomaly called *write skew*. True serializability needs extra machinery on top (e.g. Postgres SSI).

## When you would reach for it

- Any OLTP workload where reads and writes happen concurrently on the same hot rows.
- Reporting queries that need a consistent view of a live, mutating dataset.
- Systems where read latency matters and you cannot afford readers waiting for write locks.
- Long analytical queries running against a transactional store.

## When you would NOT reach for it

- You do not, really — for OLTP databases MVCC is essentially the default. The question is usually *which* MVCC implementation, not whether to use one.
- Workloads dominated by tiny, single-row updates with no contention may find pure in-place updates slightly cheaper (less bloat, less GC).
- Embedded or single-writer stores (think SQLite in many setups) where there is no concurrency to manage.

## Key vocabulary (just enough to keep reading)

- **Tuple / row version** — one historical copy of a row.
- **Snapshot** — the set of transaction IDs a transaction considers "already committed and visible."
- **xmin / xmax** — Postgres's per-row stamps for "who created me" and "who deleted me."
- **Undo log** — InnoDB/Oracle's way of reconstructing old versions on demand instead of storing them inline.
- **Visibility rules** — the logic that decides which version of a row a given transaction sees.
- **Snapshot isolation** — the isolation level MVCC naturally provides.
- **Write skew** — the anomaly snapshot isolation does not prevent.
- **VACUUM / purge** — the background job that reclaims space from row versions no transaction can still see.
- **Bloat** — the disk-space cost of accumulated dead versions.
- **Snapshot-too-old** — the error you get when a long-running transaction needs a version that has already been purged.

## What's next

The next document (`02-deep-dive.md`) answers **What / Where / When / How / Why** in detail — how Postgres's xmin/xmax tuples differ from InnoDB's undo-log approach, how snapshots are actually built, where bloat comes from, and what snapshot isolation does and does not guarantee.
