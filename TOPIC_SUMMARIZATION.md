# Write-Ahead Log

A Write-Ahead Log (WAL) is a durable, append-only journal where a database records what it is *about to* change, before it actually changes anything. The rule is simple: every intended change is written to a sequential log file on disk first, and only then is the in-memory data allowed to change. If the process crashes mid-flight, restart reads the log and replays it to reconstruct exactly the state the committed transactions promised.

An engineer reaches for a WAL any time a storage system must survive `kill -9` or a power cut without losing acknowledged writes. It gives you atomic multi-page updates, fast commits by batching many small random writes into one sequential log flush, and a natural feed for point-in-time recovery and streaming replication. It is not a backup, not an audit log, and not the application-level "transaction log" that event sourcing borrows the idea from — it is a storage-engine primitive.

Picture a bank teller with two things on the desk: a bound ledger notebook and the actual cash drawers. Whenever a customer says "move $50 from A to B," the teller writes the transfer into the notebook, waits for the vault clerk's nod, and only then moves the cash. If the building burns down that night and the drawers melt, the notebook survives, and tomorrow the branch rebuilds every account balance by replaying entries in order. That notebook is the WAL. The cash drawers are the data pages. The vault clerk's nod is `fsync`.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/database/write-ahead-log/
