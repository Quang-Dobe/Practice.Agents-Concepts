# Write-Ahead Log — Overview

> A Write-Ahead Log (WAL) is an append-only file where a database writes "I am about to do X" before it actually does X, so that a crash can never lose a committed change.

## The 30-second version
Databases live with two facts that hate each other: disks are slow, and users expect their committed data to survive a crash. Writing every change directly to its final on-disk location is too slow; keeping changes in memory until later is too risky. The WAL is the compromise — every change is first appended to a sequential log file and fsync'd to disk. Only then is the transaction considered committed. The actual data pages get updated later, lazily. If the process dies, recovery replays the log and the database wakes up consistent.

## The mental model
Picture a busy restaurant kitchen. Customers (transactions) shout orders. The kitchen could run each order to the pantry, fetch ingredients, cook, and plate before taking the next — correct, but catastrophically slow. Instead, there is one server with a notepad at the pass. Every order goes onto the notepad in order, the page is torn off and clipped to the rail, and only then does the kitchen tell the customer "got it." The cooks work from the rail at their own pace, in whatever order is efficient.

If the kitchen burns down mid-shift and reopens tomorrow, the notepad is the source of truth. They re-read every ticket from the last known clean state and redo the work.

That notepad is the WAL. Fast because it is append-only (one sequential write, no seeking around the disk). Safe because nothing is "done" until it is on the notepad. Flexible because the actual cooking — writing dirty pages back to the table files — can be batched, reordered, and deferred.

## What it is NOT
- Not a backup. A WAL only covers changes since the last checkpoint or base backup; you still need full backups.
- Not the same as an application-level audit log. WAL records are physical or logical *page-level* changes meant for the engine itself, not for humans.
- Not a queue or message broker. Kafka borrows the log shape, but a WAL is bound to one database's storage engine.
- Not the binlog. MySQL's binlog is for replication and point-in-time recovery; InnoDB's redo log is the actual WAL. Two different files, two different jobs.
- Not optional in any serious OLTP system. If you turn it off, you have given up the D in ACID.

## When you would reach for it
- You are designing any system that must survive `kill -9`, a power loss, or a kernel panic without losing acknowledged writes.
- You want fast commits without giving up durability — sequential log appends are far cheaper than random page writes.
- You need point-in-time recovery, streaming replication, or change-data-capture; all three are built on top of the WAL stream.
- You are building a custom storage engine, embedded key-value store, or stateful service that owns its own on-disk format.

## When you would NOT reach for it
- Pure in-memory caches where losing state on restart is acceptable (Redis without AOF, Memcached).
- Stateless services. Logging your own WAL on top of Postgres is just reinventing the wheel one layer up.
- Analytical/columnar systems doing bulk loads where each load is idempotent and re-runnable from source — the overhead may not be worth it.

## Key vocabulary (just enough to keep reading)
- **LSN (Log Sequence Number)** — monotonic offset identifying a position in the log.
- **fsync** — the syscall that forces buffered writes to physical disk; the WAL's whole guarantee depends on it.
- **Checkpoint** — a marker saying "all dirty pages up to this LSN are now safely in the data files," which lets older WAL segments be recycled.
- **Redo** — replaying committed changes from the log after a crash.
- **Undo** — rolling back changes from in-flight transactions that never committed.
- **Dirty page** — an in-memory page whose changes are recorded in the WAL but not yet written to the table file.
- **Group commit** — batching many transactions into a single fsync to amortize cost.
- **Physical vs. logical logging** — physical logs byte-level page diffs (Postgres, InnoDB redo); logical logs row-level operations (MySQL binlog, logical replication).

Real-world examples worth knowing by name: PostgreSQL's WAL (`pg_wal/`), SQLite's WAL mode (`-wal` file alongside the db), MySQL InnoDB's redo log (`ib_logfile*`), and RocksDB's WAL feeding its LSM tree. Different shapes, same idea.

## What's next
The next document, `02-deep-dive.md`, answers What / Where / When / How / Why in detail — record formats, the ARIES recovery algorithm, checkpointing strategies, fsync gotchas, and how replication piggybacks on the log stream.
