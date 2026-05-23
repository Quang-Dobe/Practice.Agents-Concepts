# LSM Tree (Log-Structured Merge Tree) — Overview

> A storage structure that makes writes cheap by never erasing in place — it just keeps appending, and tidies up later in the background.

## The 30-second version
An LSM tree is the engine behind write-heavy databases like Cassandra, RocksDB, LevelDB, and ScyllaDB. Its core trick: instead of finding a record on disk and overwriting it (which means slow, scattered random writes), it buffers changes in memory and flushes them to disk in big sequential batches. Reads get a little harder — you may have to check several places for the latest value — and a background process called *compaction* constantly merges those batches to keep things tidy. You care because it's the standard answer to "I have a firehose of writes and disks that hate random I/O."

## The mental model
Think of a busy chef during a dinner rush. Orders come in faster than anyone can file them neatly. So the chef scribbles each new order on a sticky note and slaps it on a board — instant, no searching. The board is **memory**.

When the board fills up, the chef doesn't throw the notes away. They grab the whole stack, sort it, and staple it into a labeled binder, then start a fresh board. Each binder is an immutable **file on disk** — written once, never edited.

Over a night you accumulate many binders. To find "table 7's current order," you check the newest binder first, then older ones, stopping at the first match — because the latest note wins. A delete is just a sticky note saying "table 7, cancelled" (a *tombstone*). Periodically, a helper merges several binders into one cleaner binder, dropping cancelled and superseded orders. That merging is **compaction**.

That's an LSM tree: fast append-only writes up front, sorted immutable files on disk, newest-wins lookups, and continuous background cleanup.

## What it is NOT
- Not a B-tree / B+-tree. Those update records in place and optimize for reads; LSM optimizes for writes.
- Not a write-ahead log. A WAL is the crash-recovery journal LSM uses *alongside* memory; the LSM is the whole indexed structure.
- Not a cache. Data here is the durable source of truth, not a throwaway speed layer.
- Not a query language or database itself. It's the storage engine underneath one.

## When you would reach for it
- Write-heavy workloads: time-series, event logs, metrics, IoT ingestion.
- Workloads where sequential disk throughput matters more than single-record read latency.
- Systems needing high insert/update rates with good compression on disk.
- When you're choosing a database (Cassandra, ScyllaDB, RocksDB-backed stores) for ingestion-first use cases.

## When you would NOT reach for it
- Read-latency-critical lookups where a B-tree's single-seek read wins.
- Heavy in-place update workloads where compaction would burn CPU and I/O for little gain.
- Small datasets that fit comfortably in a simpler indexed structure.
- Latency-sensitive systems that can't tolerate occasional compaction-induced stalls.

## Key vocabulary (just enough to keep reading)
- **Memtable** — the in-memory, sorted buffer where new writes land first.
- **WAL (write-ahead log)** — an append-only crash-recovery log written before acknowledging a write.
- **SSTable** — Sorted String Table; an immutable, sorted file on disk (a "binder").
- **Flush** — moving a full memtable out to disk as a new SSTable.
- **Compaction** — merging SSTables to drop stale/deleted data and reduce read fan-out.
- **Tombstone** — a marker recording that a key was deleted.
- **Write amplification** — extra bytes physically written (via compaction) per logical write.
- **Read amplification** — extra files checked to answer one read.
- **Bloom filter** — a probabilistic index that skips SSTables that definitely lack a key.

## What's next
The next document answers What / Where / When / How / Why in detail — the levels, the compaction strategies (size-tiered vs leveled), the read path with Bloom filters, and the amplification trade-offs only sketched here.
