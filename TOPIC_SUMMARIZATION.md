# Write-Ahead Log

A Write-Ahead Log, or WAL, is a durability trick that databases and storage engines use to make sure a committed write is never lost, even if the process crashes a millisecond later. Every change is first appended as a small record to a sequential on-disk log file, and only after that record is safely flushed to physical storage does the system confirm the commit. The real data pages get updated later, in the background.

Engineers reach for a WAL whenever they need durable, atomic writes without paying the cost of scattered random I/O on every commit. It is the pattern underneath Postgres, MySQL's InnoDB, SQLite in WAL mode, RocksDB, and even filesystem journals like ext4. The same log doubles as a free source of truth for point-in-time recovery, streaming replication, and change data capture — you already wrote the changes down, so you may as well ship them.

The mental model is a busy kitchen ticket rail. When an order comes in, the waiter clips a ticket to the rail in order; that single act is fast and cannot be lost. The chef then cooks the dishes in whatever order is efficient. If the power flickers mid-service, the chef just walks back to the rail and reads the tickets in order. The WAL is the rail, log records are the tickets, and the data files are the plated dishes that eventually catch up.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/database/write-ahead-log/
