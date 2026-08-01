# Eventual Consistency — Overview

> A promise that all copies of your data will agree with each other *eventually* — not right now, not at the same moment, but soon enough that the system stays usable while it catches up.

## The 30-second version

In a distributed system, the same piece of data lives on many machines. Keeping every copy identical *at every instant* is slow and fragile — it requires all machines to coordinate on every write. Eventual consistency drops that requirement. Writes are accepted quickly on whichever machine you reached, then quietly propagate to the others in the background. For a brief window, different users may see different values. Given a quiet moment with no new writes, all replicas converge to the same state. You trade the illusion of a single, instantly-updated truth for availability, low latency, and the ability to survive network trouble.

## The mental model

Think of a big company with offices in ten cities. Every office has its own copy of the employee handbook. When HR changes a policy in the New York office, they don't fly to every city to update every binder before anyone is allowed to read it. Instead, they update the New York copy, and the change ripples out via internal mail over the next few days.

During those few days, someone in Tokyo who opens their local binder will see the *old* policy. That is not a bug. It is the point. The Tokyo office keeps working, keeps serving employees, and simply catches up when the memo arrives. Once the mail is delivered and nothing else changes, every binder in every city says the same thing again.

Eventual consistency is that mail system, generalized. Reads are fast because they hit the nearest copy. Writes are fast because they don't wait for a global handshake. The system tolerates network hiccups because each office can keep operating on its own copy. The cost is a temporary window where different observers see different truths.

## What it is NOT

- Not **strong consistency**. Strong consistency guarantees every read sees the latest write, immediately, everywhere — at the cost of coordination and latency.
- Not **no consistency**. There is a real convergence guarantee; replicas do not drift forever.
- Not the same as **asynchronous replication**. Async replication is a *mechanism*; eventual consistency is the *contract* you expose to users.
- Not a synonym for **BASE** or **NoSQL**. Many BASE systems are eventually consistent, but the model predates and outlives that marketing.

## When you would reach for it

- Global read-heavy systems where users tolerate slightly stale data (social feeds, product catalogs, DNS).
- Multi-region deployments where cross-region write coordination would blow your latency budget.
- Systems that must keep serving reads and writes during a network partition.
- Analytics and counters where the exact value at this millisecond does not matter.

## When you would NOT reach for it

- Anything with a hard invariant that must never be violated (bank balances that cannot go negative, seat inventory that cannot be double-sold, unique username registration).
- Workflows where a user writes something and immediately reads it back expecting to see their own change (unless you add read-your-writes guarantees on top).
- Small, single-region systems where a plain leader-based database already gives you strong consistency for free.

## Key vocabulary (just enough to keep reading)

- **Replica** — one physical copy of the data on one node.
- **Convergence** — the state where all replicas agree again.
- **Propagation** — the background process that ships writes between replicas.
- **Stale read** — a read that returns an older value because the local replica has not caught up.
- **Conflict** — two replicas accepted different writes to the same key and must reconcile.
- **Anti-entropy** — background repair processes (gossip, Merkle-tree sync) that push replicas toward convergence.
- **CAP theorem** — the result that forces the trade-off: pick two of Consistency, Availability, Partition-tolerance.
- **CRDT** — a data structure designed so concurrent writes always merge cleanly, no conflict resolution needed.
- **Read-your-writes** — a stronger guarantee layered on top: a client always sees its own recent writes.
- **Quorum** — reading/writing to enough replicas that overlap guarantees you touch the latest value.

## What's next

The next document (`02-deep-dive.md`) answers the What / Where / When / How / Why in detail — the CAP and PACELC trade-offs, propagation mechanics, conflict resolution strategies (last-write-wins, vector clocks, CRDTs), and where real systems like DynamoDB, Cassandra, and S3 sit on the spectrum.
