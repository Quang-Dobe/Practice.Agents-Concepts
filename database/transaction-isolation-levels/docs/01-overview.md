# Transaction Isolation Levels — Overview

> An isolation level is the setting that controls how much of other transactions' in-flight and freshly committed work your SQL transaction can see, trading correctness for concurrency.

## The 30-second version

When two transactions touch the same data at the same time, the database has to decide what each one is allowed to see. Perfect isolation, where every transaction acts as if it ran alone, costs throughput, so SQL gives you a dial with four standard stops: Read Uncommitted, Read Committed, Repeatable Read, Serializable. Each stop forbids a specific set of anomalies, meaning wrong results caused by interleaving. You care because the default dial setting in your database almost certainly allows some of these anomalies, and the bugs they cause show up under load, are hard to reproduce, and look like "impossible" data.

## The mental model

Picture an office where several clerks update one shared paper ledger. Each clerk works on a task (a transaction) that involves several reads and writes.

- **Read Uncommitted**: you can read a colleague's pencil marks while they are still writing. If they erase them, you acted on numbers that never existed.
- **Read Committed**: you only see entries written in ink, meaning committed. But every time you look up, the ledger may have changed. Read the balance twice in one task and you can get two answers.
- **Repeatable Read**: the moment you start, you get a photocopy of the ledger. Your reads never move. The catch is that you write on the real ledger, and your photocopy cannot tell you what other clerks are writing right now.
- **Serializable**: the result is guaranteed to match some order in which the clerks took turns, one at a time. The office may still let clerks work in parallel, but if their work cannot be lined up into a valid order, one clerk is told "start over."

The photocopy trap is the one that bites. Two on-call doctors each check "is at least one other doctor on call?", each sees "yes" on their own photocopy, and each signs off. Both writes succeed and nobody is on call. This is **write skew**, and only Serializable reliably stops it.

## What it is NOT

- **Not atomicity.** Atomicity is all-or-nothing for one transaction. Isolation is about how concurrent transactions see each other.
- **Not durability or replication consistency.** "Read-your-writes" and eventual consistency describe replicas. Isolation levels describe transactions within one database.
- **Not the same across engines.** The names come from the ANSI SQL standard, but PostgreSQL, MySQL InnoDB, SQL Server, and Oracle each implement them differently. PostgreSQL's Repeatable Read is really Snapshot Isolation, and Oracle's "Serializable" is too.
- **Not MVCC.** MVCC (multi-version concurrency control) is one technique engines use to implement several of these levels. It has its own topic in this repo.
- **Not a replacement for locks you take on purpose.** `SELECT ... FOR UPDATE` and unique constraints are often the better fix for a specific race.

## When you would reach for it

- Money, inventory, or seat-booking logic where two requests can read-then-write the same data at the same time.
- Invariants spread across rows ("at least one admin", "total allocation ≤ budget") that no single-row constraint can enforce.
- Reports that run several queries and need them all to agree with each other (Repeatable Read or a snapshot).
- Debugging duplicate charges, negative stock, or counters that "lose" increments under load.

## When you would NOT reach for it

- Single-statement updates like `UPDATE stock SET qty = qty - 1 WHERE id = 7 AND qty > 0`. The row lock already makes these safe.
- Cranking everything to Serializable "just in case." You pay in retries and aborts, and your code must handle serialization failures or it breaks.
- Read Uncommitted for "speed." PostgreSQL does not even implement it (it behaves as Read Committed), and elsewhere it gives you wrong answers, not faster ones.

## Key vocabulary (just enough to keep reading)

- **Anomaly**: an incorrect result that can only happen because transactions interleaved.
- **Dirty read**: seeing another transaction's uncommitted write.
- **Non-repeatable read**: reading the same row twice and getting different values.
- **Phantom read**: re-running a query and getting a different set of rows.
- **Lost update**: two read-modify-write cycles overlap and one write silently overwrites the other.
- **Write skew**: two transactions read overlapping data, write different rows, and together break an invariant.
- **Snapshot Isolation**: every transaction reads from a frozen point-in-time view. Not one of the four ANSI levels, but what many engines actually ship.
- **Serialization failure**: the error a Serializable transaction gets when the engine aborts it to preserve correctness. The client is expected to retry.
- **Default level**: Read Committed in PostgreSQL, SQL Server, and Oracle. Repeatable Read in MySQL InnoDB.

## What's next

The next document answers What / Where / When / How / Why in detail: the full anomaly-by-level matrix, how locking and snapshot implementations differ, exactly where PostgreSQL, MySQL InnoDB, and SQL Server depart from the ANSI definitions, and how Serializable Snapshot Isolation catches write skew without locking everything.
