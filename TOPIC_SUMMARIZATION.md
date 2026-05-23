# LSM Tree (Log-Structured Merge Tree)

An LSM tree is a storage structure that makes writes cheap by never erasing data in place. Instead of hunting down a record on disk and overwriting it, it buffers incoming changes in memory and later flushes them to disk in big sequential batches, tidying everything up in the background. It is the engine sitting underneath write-heavy databases like Cassandra, RocksDB, LevelDB, and ScyllaDB.

Engineers reach for it whenever they have a firehose of writes and disks that hate scattered random I/O — time-series data, event logs, metrics, and IoT ingestion are the classic cases. The trade-off is that reads get a little harder, because the newest value for a key might live in memory or in any of several on-disk files, so a lookup may have to check more than one place. A background process called compaction continuously merges those files to keep read fan-out low and reclaim space from deleted or superseded data. It is the opposite design choice from a B-tree, which updates records in place and optimizes for fast single-seek reads.

Picture a busy chef during a dinner rush: each new order gets scribbled on a sticky note and slapped on a board instantly, with no searching. When the board fills, the chef sorts the whole stack and staples it into a labeled binder, then starts fresh. To find a table's current order you check the newest binder first and stop at the first match, because the latest note wins. A cancellation is just another note (a tombstone), and periodically a helper merges binders into cleaner ones. That is an LSM tree.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/database/lsm-tree/
