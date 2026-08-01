# Eventual Consistency

Eventual consistency is a promise that all copies of your data will agree with each other *eventually* — not right now, not at the same moment, but soon enough that the system stays usable while it catches up. In a distributed system, the same piece of data lives on many machines. Instead of forcing every write to coordinate globally before it is accepted, an eventually consistent system takes the write on whichever machine you reached and quietly propagates it to the others in the background. For a brief window, different users may see different values; once the writes stop flowing, every replica converges on the same state.

An engineer reaches for eventual consistency when they need low latency and high availability across a fleet of replicas — global read-heavy systems where users tolerate slightly stale data (social feeds, product catalogs, DNS), multi-region deployments where cross-region coordination would blow the latency budget, and systems that must keep serving reads and writes during a network partition. It is the wrong tool for hard invariants that must never be violated (bank balances that cannot go negative, seat inventory that cannot be double-sold) — those need strong consistency and its coordination cost.

Picture a big company with offices in ten cities. Every office keeps its own copy of the employee handbook. When HR changes a policy in the New York office, they do not fly to every city to update every binder before anyone is allowed to read it. They update the New York copy, and the change ripples out via internal mail over a few days. During those days, someone in Tokyo who opens their local binder still sees the old policy — not a bug, the whole point. The Tokyo office keeps working and simply catches up when the memo arrives.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/general-concept/eventual-consistency/
