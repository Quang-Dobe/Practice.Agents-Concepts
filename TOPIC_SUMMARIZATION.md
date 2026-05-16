# MVCC (Multi-Version Concurrency Control)

MVCC is the technique modern databases use to let many transactions read and write the same data at the same time without making everyone wait their turn. Instead of locking a row while one transaction is changing it, the database keeps the old copy of the row around and creates a new version alongside it, then shows each transaction the snapshot of the data that was true at the moment it started.

You reach for MVCC any time a system needs concurrent reads and writes on the same hot rows without sacrificing correctness. It is the default in Postgres, MySQL/InnoDB, Oracle, SQL Server (under snapshot isolation), and MongoDB's WiredTiger engine, so most engineers are already relying on it whether they think about it or not. Understanding how it works matters when long-running transactions cause table bloat, when readers and writers somehow still seem to interfere, or when a "safe" transaction produces a wrong answer because snapshot isolation is not actually serializable.

A useful analogy is a shared Google Doc with version history always on. If Alice opens the doc at 9:00 and Bob edits and saves at 9:01, Alice keeps seeing the 9:00 version until she refreshes — Bob's edit is a new revision sitting alongside the old one, not an overwrite. Anyone who opens the doc at 9:02 sees the new version. That is exactly what an MVCC database is doing with rows: never overwriting in place, always writing a new tagged version, and letting each transaction pick the version that was visible at its own snapshot.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/database/mvcc/
