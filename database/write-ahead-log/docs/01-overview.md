# Write-Ahead Log — Overview

> A Write-Ahead Log (WAL) is a durability trick where a database appends every change to a sequential on-disk log **before** touching the actual data files, so a crash can never lose a committed write.

## The 30-second version

Every mutation — an `INSERT`, an index update, a page split — is first written as a small record to an append-only file. Only after that record is safely on disk (`fsync`'d) does the database confirm the commit to the client. The real data pages get updated later, in the background. If the process dies mid-flight, recovery just replays the log and the database wakes up in a consistent state. This one idea is how Postgres, MySQL/InnoDB, SQLite, RocksDB, and even ext4 all keep their promises without writing everything twice at full speed.

## The mental model

Picture a busy restaurant kitchen. Orders come in fast. If the head chef tried to plate every dish the moment it was ordered — walking to the pantry, fetching ingredients from three different shelves, chopping, cooking, plating — the line would collapse.

Instead there's a **ticket rail**. The moment an order is taken, the waiter clips a ticket to the rail in order. That single act is atomic and fast. Now the kitchen can cook in whatever order is most efficient — batching, parallelizing, using the oven while the pan heats up. And if the power flickers and the chef forgets what she was doing, she just walks back to the rail and reads the tickets in order. No order is lost.

The WAL is the ticket rail. The tickets are log records. The plated dishes are the data pages on disk. The rail is written in one direction, sequentially, and that is the whole point: **sequential writes to one file are dramatically faster than scattered random writes across many files.** WAL turns durability from an expensive random-I/O problem into a cheap append.

The leave-with-this insight: during the window between "commit" and "checkpoint," the log *is* the source of truth. The data files are lagging projections that will eventually catch up.

## What it is NOT

- **Not a replication log** (though it's often reused as one). Replication is a downstream consumer of the WAL, not its purpose.
- **Not an audit log.** WAL records are physical or logical change records for recovery, not human-readable history.
- **Not the same as a redo log alone.** Some engines (InnoDB) split redo and undo; WAL is the general pattern that covers both approaches.
- **Not a backup.** The log is truncated once its changes are safely in the data files. It only protects against crashes, not against `DROP TABLE`.

## When you would reach for it

- You are building any storage engine that promises durability of committed writes.
- You need atomic multi-page updates (a B-tree split, a transaction touching multiple rows) without writing each page twice.
- You want cheap point-in-time recovery or streaming replication — the log is already there, just ship it.
- You need fast commits on spinning disks or network-attached storage where random I/O is punishingly slow.

## When you would NOT reach for it

- Pure in-memory caches where losing data on crash is acceptable (Redis without AOF, memcached).
- Append-only immutable stores where the "data file" already *is* a sequential log (Kafka segments, event stores) — the WAL pattern is baked in, not bolted on.
- Tiny embedded configs where a full rewrite of the file is faster than log-and-checkpoint machinery.

## Key vocabulary (just enough to keep reading)

- **Log record** — one entry describing a single change (which page, old value, new value, or a logical op).
- **LSN (Log Sequence Number)** — a monotonically increasing ID for each record; the log's clock.
- **fsync** — the OS call that forces buffered writes to physical storage. WAL's guarantee lives or dies on this.
- **Checkpoint** — a point where dirty data pages are flushed to disk and the log up to that LSN becomes safe to discard.
- **Redo** — replay a committed change during recovery.
- **Undo** — roll back an uncommitted change during recovery.
- **Group commit** — batching many transactions' fsyncs together to amortize disk cost.
- **WAL mode (SQLite)** — SQLite's specific opt-in variant that swaps its default rollback-journal for a proper WAL file.

## What's next

The next document, `02-deep-dive.md`, walks through What / Where / When / How / Why in detail: the anatomy of a log record, the commit-and-checkpoint lifecycle, how recovery actually replays, and the trade-offs different engines make (physical vs. logical logging, ARIES-style undo/redo, group commit tuning).
