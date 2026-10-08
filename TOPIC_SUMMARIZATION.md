# Transaction Isolation Levels

A transaction isolation level is the setting that controls how much of other transactions' unfinished and freshly committed work your SQL transaction can see. SQL offers a dial with four standard stops: Read Uncommitted, Read Committed, Repeatable Read, and Serializable. Each stop forbids a specific set of anomalies, which are wrong results caused by transactions interleaving, and each stronger stop trades some concurrency for correctness.

It matters because the default setting in your database almost certainly allows some of these anomalies: Read Committed in PostgreSQL, SQL Server, and Oracle, and Repeatable Read in MySQL InnoDB. The resulting bugs show up under load, are hard to reproduce, and look like impossible data. Engineers reach for isolation levels when money, inventory, or booking logic reads and then writes shared data, when an invariant spans several rows, or when debugging duplicate charges, negative stock, or lost increments. The same level names also behave differently across engines, so the name alone is not a guarantee.

Picture clerks sharing one paper ledger. At Read Committed you only see ink, but the ledger can change between two glances. At Repeatable Read you work from a photocopy taken when you started. That photocopy is the trap: two on-call doctors each check that another doctor is on call, each sees yes on their own copy, and both sign off, leaving nobody on call. This is write skew, and only Serializable reliably stops it, by telling one of them to start over.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/database/transaction-isolation-levels/present/index.html
