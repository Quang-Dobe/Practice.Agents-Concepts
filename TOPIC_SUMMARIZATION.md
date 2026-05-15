# Write-Ahead Log

A Write-Ahead Log, or WAL, is an append-only file where a database writes down "I am about to do X" before it actually does X. Only after that note is safely on disk is the change considered committed. The real data files get updated later, lazily, in whatever order is efficient.

It matters because disks are slow and crashes are common, and those two facts pull a database in opposite directions. Updating every row in its final on-disk location for every commit is too expensive; keeping changes only in memory means a single power loss can lose acknowledged writes. The WAL is the compromise that resolves the tension — one sequential append plus an fsync gives you fast commits and durable data at the same time. Engineers reach for a WAL whenever a system must survive a `kill -9`, a kernel panic, or a power cut without forgetting what it told its users it remembered, and whenever they need building blocks like point-in-time recovery, streaming replication, or change-data-capture, all of which are read off the same log stream.

Picture a busy kitchen. Every order goes onto a notepad at the pass and the customer only hears "got it" once the ticket is clipped to the rail. The cooks work from the rail at their own pace. If the kitchen burns down overnight, tomorrow's crew rebuilds the night's state by re-reading the tickets. That notepad is the WAL: cheap to write, expensive to lose, and the single source of truth when things go wrong.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/database/write-ahead-log/
