# Two-Phase Commit — Overview

> Two-phase commit (2PC) is a protocol that lets one transaction span several databases or services and guarantees they either all commit or all roll back, by first asking everyone "can you commit?" and only then telling everyone "commit."

## The 30-second version

A single database gives you atomicity for free: the whole transaction lands, or none of it does. Once one business operation touches two systems, for example debiting an account in Postgres A and crediting one in Postgres B, that guarantee is gone. If A commits and B crashes, money disappears. 2PC adds a **coordinator** that runs a vote before anyone commits for real. It is the classic answer to atomic commit across nodes. It is also known for one weakness: if the coordinator dies at the wrong moment, everyone else is stuck waiting.

## The mental model

Think of a wedding ceremony.

The officiant (the **coordinator**) does not declare anyone married right away. First they ask each partner: "Do you take this person?" Each partner (a **participant**) thinks it over and answers "I do" or "I don't." Once a partner says "I do," they have given up the right to leave. They are bound to whatever the officiant says next.

- **Phase 1 (prepare / vote):** the officiant asks everyone. Each participant does the work, writes it durably to disk, holds its locks, and votes.
- **Phase 2 (commit / abort):** if every answer was "I do," the officiant says "I now pronounce you married" and everyone commits. If even one said "I don't," the ceremony is off and everyone rolls back.

```
Coordinator          DB A            DB B
    |--- PREPARE ------>|               |
    |--- PREPARE ---------------------->|
    |<-- YES -----------|               |
    |<-- YES ---------------------------|
    |   (writes "COMMIT" to its own log)
    |--- COMMIT ------->|               |
    |--- COMMIT ----------------------->|
```

Now the weak spot. Both partners said "I do," and the officiant faints before saying the final line. The partners can't walk away, because the other one might already have heard "pronounced." They can't assume they're married either. So they stand there, holding their locks, until the officiant wakes up. That is the **blocking problem**, and it explains most of 2PC's reputation.

## What it is NOT

- Not consensus (Paxos, Raft). Consensus needs a majority to agree on a value; 2PC needs *every* participant to vote yes, and one failure can stall it.
- Not a Saga. A Saga commits each step locally and undoes mistakes with compensating actions afterwards. 2PC never commits anything until all participants agree.
- Not two-phase locking (2PL). 2PL is a concurrency-control rule *inside* one database. The names are similar; the ideas are not.
- Not three-phase commit (3PC). 3PC adds a phase to reduce blocking and is rarely used in practice.

## When you would reach for it

- You need one atomic write across two or more relational databases you control, such as a sharded Postgres cluster.
- You commit to a database and a message broker together, and both support XA (the standard 2PC interface).
- You are building a distributed database. Google Spanner and CockroachDB run commit protocols in the 2PC family underneath.
- The number of participants is small, they share a fast, reliable network, and correctness matters more than latency.

## When you would NOT reach for it

- Your participants are independent microservices owned by different teams. Locks held across services couple their availability.
- One of the participants is a third-party HTTP API. It can't "prepare" and then wait for your decision.
- You need high throughput or low latency. Every transaction pays at least two network round trips plus forced disk writes, and holds locks the whole time.
- Eventual consistency is acceptable. The outbox pattern or a Saga will be simpler and more available.

## Key vocabulary (just enough to keep reading)

- **Coordinator (transaction manager):** the node that runs the vote and records the final decision.
- **Participant (resource manager):** a database or broker that does the work and votes.
- **Prepare / vote:** phase 1, where a participant persists its work and promises it can commit.
- **Prepared (in-doubt) transaction:** one that voted yes and is still waiting for the decision. It holds locks until then.
- **Commit point:** the moment the coordinator durably logs "COMMIT." After this, the outcome can't change.
- **Blocking:** participants stuck in-doubt because the coordinator is unreachable.
- **XA:** the X/Open standard API for 2PC, used by JDBC drivers, MySQL, and message brokers.
- **`PREPARE TRANSACTION`:** PostgreSQL's native 2PC command, followed later by `COMMIT PREPARED` or `ROLLBACK PREPARED`. It is off by default (`max_prepared_transactions = 0`).

## What's next

The next document answers What / Where / When / How / Why in detail: the exact message flow and log writes, what happens at each possible crash point, how real systems recover in-doubt transactions, and why so many teams pick Sagas instead.
