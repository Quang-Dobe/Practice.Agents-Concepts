# Write-Ahead Log — Overview

> A Write-Ahead Log (WAL) is a durable, append-only journal where a database records what it is *about to* change, before it actually changes anything — so a crash can never lose committed work.

## The 30-second version

Databases hold their "live" state in memory for speed. Memory disappears when the power fails. The WAL is the safety net: every intended change is written to a sequential log file on disk *first*, and only then is the in-memory data allowed to change (and eventually get written back to the real data files, lazily). If the process dies mid-flight, restart reads the log and replays it to reconstruct exactly the state the committed transactions promised. This is how "your `INSERT` returned OK" translates into "your row will still be there tomorrow."

## The mental model

Picture a bank teller with two things on the desk: a **bound ledger notebook** (pages numbered, entries only appended, never erased) and the **actual cash drawers** for each account. Whenever a customer says "move $50 from A to B," the teller does not touch the drawers first. She writes the transfer into the notebook, closes it, hands it to the vault clerk, waits for a nod that says "your line is now permanent" — *then* she moves the cash. If the building burns down that night and the drawers melt, the notebook survives, and tomorrow the branch can rebuild every account balance by replaying entries in order.

That notebook is the WAL. The cash drawers are the data pages. The vault clerk's nod is `fsync`. The rebuild the next morning is crash recovery.

The trick that makes this fast: writing to the notebook is a **sequential append** — the fastest thing a disk can do — while updating the drawers is random access and can be deferred, batched, and reordered. The database gets to lie about when data files hit disk, and still keep its ACID promise, because the log already tells the true story.

## What it is NOT

- Not a **backup**. Backups are point-in-time snapshots you keep for weeks; a WAL is a rolling record measured in seconds-to-hours that the engine actively uses.
- Not the **binary log / replication log**. Those describe *logical* changes for other servers to consume; a WAL describes *physical* page-level changes for this server's own recovery. (Some systems, like Postgres, use the WAL for both — but the roles are distinct.)
- Not an **audit log**. Audit logs are for humans and compliance; the WAL is for the engine itself and is usually unreadable without engine internals.
- Not a **transaction log in the application sense**. Event sourcing, message queues, and Kafka topics borrow the *idea*, but a database WAL is an implementation detail of storage, not an application-level API.

## When you would reach for it

- You are building any storage system that must survive `kill -9` or a power cut without losing acknowledged writes.
- You need atomic multi-page updates (a single logical change touches several data pages, and either all or none must apply).
- You want fast commits: batching many small random writes into one sequential log flush.
- You need point-in-time recovery or streaming replication — the log is the natural feed for both.

## When you would NOT reach for it

- Pure in-memory caches where losing data on restart is acceptable (Redis without AOF, memcached).
- Tiny embedded configs where a full rewrite of the file on every change is simpler and safe enough.
- Read-only or append-only-file workloads where the data file *is* already the log.
- Systems where the extra fsync latency is unacceptable and durability is explicitly traded away.

## Key vocabulary (just enough to keep reading)

- **LSN (Log Sequence Number)** — monotonically increasing ID stamped on every log record; the log's timeline.
- **fsync** — the OS call that forces buffered writes down to physical storage; the moment durability becomes real.
- **Dirty page** — a data page modified in memory but not yet written back to the data file.
- **Flush** — writing dirty pages from memory back to their data files (separate from log writes).
- **Checkpoint** — a periodic marker saying "everything up to this LSN is safely in the data files"; bounds how much log recovery must replay.
- **REDO** — replaying log records to re-apply committed changes that never reached the data file.
- **UNDO** — reversing log records to roll back changes from transactions that were still in-flight at crash time.
- **Commit record** — the log entry whose durable arrival is the exact instant a transaction is officially committed.
- **Log tail** — the newest, still-being-written end of the log.

## What's next

The next document, `02-deep-dive.md`, answers *What / Where / When / How / Why* in detail — the anatomy of a log record, the write path from `BEGIN` to `COMMIT`, how checkpoints keep recovery bounded, the REDO/UNDO recovery pass, and how real engines (Postgres, InnoDB, SQLite) implement each piece.
