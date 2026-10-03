# Two-Phase Commit

Two-phase commit (2PC) is a protocol that lets one transaction span several databases or services and still land all-or-nothing. A coordinator first asks every participant "can you commit?", and only after everyone says yes does it tell them all to commit; if even one says no, everyone rolls back.

A single database gives you atomicity for free, but once one business operation touches two systems, that guarantee is gone. If you debit an account in one Postgres database and credit another in a second one, and the first commits while the second crashes, money disappears. Engineers reach for 2PC when they need one atomic write across a small number of databases or brokers they control, on a fast and reliable network, and correctness matters more than latency. It costs at least two network round trips plus forced disk writes, and locks are held the whole time, so independent microservices and third-party APIs are usually better served by a Saga or the outbox pattern.

Think of a wedding ceremony. The officiant asks each partner "Do you take this person?", and once a partner says "I do" they can no longer walk away. Only when both have said yes does the officiant pronounce them married. The weak spot shows up if the officiant faints right before that final line: both partners are stuck waiting, unable to leave and unable to assume they are married. That is the blocking problem, and it explains most of 2PC's reputation.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/database/two-phase-commit/present/index.html
