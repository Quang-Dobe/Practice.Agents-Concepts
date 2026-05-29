# CAP Theorem — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

CAP began as Eric Brewer's 2000 PODC keynote conjecture and became a theorem in 2002 when Seth Gilbert and Nancy Lynch published *Brewer's conjecture and the feasibility of consistent, available, partition-tolerant web services*. Their formalisation pins down each letter:

- **C — Consistency** is **linearizability** (precisely: one-copy linearizability over a replicated register). Every read sees the most recent completed write, as if there were a single copy of the data and operations happened in some real-time order. This is the strongest single-object consistency model — strictly stronger than sequential consistency, causal consistency, or eventual consistency.
- **A — Availability** is the property that *every request received by a non-failing node returns a non-error response*. There is no time bound stated, but the response must eventually arrive without an error. Latency is not part of the definition; refusing to answer is.
- **P — Partition tolerance** means the network is allowed to **drop or arbitrarily delay any subset of messages** between nodes. The system model is an asynchronous network — no shared clock, no message-delivery guarantee.

The theorem: no implementation of a read-write register in an asynchronous network can satisfy all three simultaneously. "Pick two of three" is a sloppy shorthand. The honest reading is: **partitions will happen; during one, you must choose either C or A for the affected data**.

### The core building blocks

- **Replica** — a node holding a copy of the data; the unit at which the partition can split.
- **Register** — the data abstraction the proof uses: a single object with `get` and `set`. Real systems generalise to key-value stores, rows, documents, ranges.
- **Linearizability** — the formal C. Operations appear to take effect atomically at some point between their invocation and response, consistent with real time.
- **Partition** — a class of network failures in which two non-empty subsets of nodes cannot exchange messages.
- **Quorum** — a subset of replicas required to acknowledge a read or write. Typically N replicas, W writes, R reads, with `R + W > N` for strong consistency.
- **Consensus** — agreement among nodes on a single value despite failures. The mechanism CP systems use to enforce linearizability across replicas (Paxos, Raft, ZAB).

### How it relates to the broader landscape

CAP belongs to the family of **impossibility results** in distributed computing — the same family as FLP (Fischer-Lynch-Paterson, 1985, which says async consensus with even one faulty process is impossible) and the Two Generals Problem. CAP's sibling and natural successor is **PACELC** (Abadi, 2010), which adds the steady-state question: *Else* (no partition), pick **L**atency or **C**onsistency. PACELC is the more honest design tool because it covers the 99.9% of operating time when the network is healthy.

## Where

### Where it lives in the stack

CAP is a property of the **data layer of a distributed system** — specifically, any system that replicates state across nodes that communicate over an unreliable network. That covers distributed databases, distributed coordination services, distributed caches, replicated file systems, and multi-region application state. It is irrelevant to single-node databases, stateless application servers, and synchronous RPC that does not replicate state.

### Where you typically encounter it

- **Coordination services (CP)** — ZooKeeper (uses ZAB), etcd (Raft), Consul (Raft). These exist precisely to give you linearizable primitives like locks and leader election.
- **Strongly consistent SQL-on-distributed (CP)** — Google Spanner, CockroachDB, YugabyteDB, FoundationDB. All use Paxos/Raft variants over data ranges.
- **Dynamo-style key-value stores (AP by default, tunable)** — Cassandra, DynamoDB, Riak, ScyllaDB.
- **Multi-model with selectable consistency** — Azure Cosmos DB exposes five named consistency levels from strong to eventual; MongoDB tunes via `readConcern`/`writeConcern`.
- **Multi-region deployments** — any active-active deployment of stateful services across regions is making a CAP choice whether the team articulates it or not.

### Ecosystem and tooling

- **For consensus**: etcd, ZooKeeper, Consul, Hashicorp Raft library, the original Lamport Paxos papers, Ongaro's Raft thesis (2014).
- **For AP conflict resolution**: Automerge and Yjs (CRDT libraries widely used in collaborative editing), Riak's built-in CRDTs, Redis CRDB (enterprise edition).
- **For testing partition behaviour**: Jepsen (Kyle Kingsbury's test framework that has embarrassed essentially every distributed database at some point), Chaos Mesh, Toxiproxy, the Linux `tc netem` and `iptables` toolchain.

## When

### When the topic emerged and why

Through the 1990s, the dominant model was a single relational database with maybe a hot standby. As internet-scale services grew, single-node systems hit ceilings and operators began sharding and replicating. Brewer's 2000 conjecture distilled what those operators had learnt empirically: you cannot have the strong-consistency semantics of a single Oracle box and also stay up when the network misbehaves at scale. Gilbert and Lynch's 2002 proof made it rigorous. The 2007 Amazon Dynamo paper then publicly demonstrated a production system that explicitly chose AP, and the NoSQL wave followed.

### When to use it as a lens

Reach for CAP when:

- Choosing a replicated datastore for a multi-region, multi-AZ, or geographically distributed deployment.
- Designing the failure-mode behaviour of a stateful service — what should happen to writes in the minority partition?
- Reviewing a vendor's "consistency" marketing claim and you need to ask "linearizable, or something weaker?".
- Reasoning about a correctness-critical operation (payments, inventory decrement, leader election) where stale reads are dangerous.

### When NOT to use it

Avoid CAP as your only lens when:

- You are operating a single node — no partitions, no theorem.
- You are picking between SQL and NoSQL on schema or query-language grounds. CAP doesn't map cleanly onto that axis.
- You need to compare two AP systems, or two CP systems, against each other. CAP groups them into the same bucket; you need finer tools (consistency models, PACELC, latency budgets).
- You need a per-operation consistency contract. CAP is too coarse; specify linearizable / causal / read-your-writes / eventual.

## How

### How it works under the hood

**The impossibility argument (Gilbert-Lynch, intuition).** Imagine two nodes, A and B, both holding a register `x = 0`. A client writes `x = 1` to A. The network partitions. A client now reads `x` from B. B cannot have received the write. If B answers (Availability), it returns the stale `0`, breaking Linearizability. If B refuses or hangs, it breaks Availability. No protocol on B can do better — it has no information about A's write. Hence C and A cannot both hold during the partition.

**How CP systems implement the choice.** They use a consensus protocol — Raft, Multi-Paxos, or ZAB — to ensure that every committed write is acknowledged by a **majority quorum** before returning success. During a partition, only the side holding a majority can elect a leader and accept writes; the minority side rejects writes (and often rejects linearizable reads as well). When the partition heals, the minority catches up via log replication. Examples: etcd, ZooKeeper, Spanner (Paxos per tablet, plus TrueTime for global ordering), CockroachDB (Raft per range).

A typical Raft write looks like:

```
1. Client -> Leader: write(k, v)
2. Leader appends to its log, sends AppendEntries to all followers
3. Once a majority (including the leader) have persisted the entry, it is "committed"
4. Leader applies to state machine, returns success to client
5. Followers apply on their next AppendEntries heartbeat
```

The minority partition cannot reach a majority, so step 3 stalls. Writes time out. That is the C-over-A choice in code.

**How AP systems implement the choice.** Dynamo-style systems replicate to N nodes and let the client choose R and W per request — *sloppy quorums*. If a target replica is unreachable, the write lands on a substitute node with a **hinted handoff** marker; the substitute delivers the data once the intended owner returns. Concurrent writes are reconciled using **vector clocks** (Dynamo, Riak) or **last-write-wins by timestamp** (Cassandra). **Read repair** and **anti-entropy** (Merkle-tree sync) fix divergence in the background. The system never refuses a write just because some replicas are unreachable.

**Tunable consistency is the norm, not the exception.** Modern systems are rarely statically CP or AP:

- Cassandra exposes `ONE`, `QUORUM`, `LOCAL_QUORUM`, `EACH_QUORUM`, `ALL`, `ANY` per query. `QUORUM` reads + `QUORUM` writes with `R + W > N` give strong consistency; `ONE/ONE` is fully AP.
- DynamoDB defaults to **eventually consistent reads** (half the RCU cost); set `ConsistentRead=true` per request for strongly consistent reads on the base table. Global secondary indexes are eventual-only.
- MongoDB picks via `readConcern` and `writeConcern` (`majority` is the strong setting).
- Cosmos DB picks one of five levels at the account or request scope.

So "Cassandra is AP" is a default, not a law.

### Key trade-offs

| Design choice | Gain | Give up |
|---|---|---|
| CP via majority consensus | Linearizable reads/writes, simple mental model | Minority partition rejects writes; write latency = slowest of majority; needs odd N |
| AP with sloppy quorum | Always writeable, low write latency | Conflicts, requires client-side or merge-time reconciliation |
| Last-write-wins | Trivial merge logic | Silent data loss on concurrent writes |
| CRDTs | Automatic conflict-free merge, strong eventual consistency | Restricted data types, larger metadata, more complex implementation |
| Leader lease (CP) | Linearizable reads off the leader without a round trip | Lease expiry causes a write stall; clock-skew sensitive |
| Quorum reads (`R + W > N`) | Strong consistency without a leader | Higher latency per operation, sensitive to slow replicas |

### Common failure modes

- **Split brain in supposedly CP systems** — two leaders accept writes simultaneously. Cause: misconfigured cluster size (even N), partial partition, or leader unable to detect it lost quorum (clock skew on leases).
- **Long GC pause looks like a partition** — a JVM stop-the-world pause longer than the heartbeat interval triggers leader election while the "dead" leader is still about to wake up and accept writes.
- **Asymmetric partition** — A can talk to B but B can't talk to A. Many heartbeat implementations get confused; some systems flap leaders.
- **AP read-your-writes violations** — client writes to one coordinator, immediately reads from a different one, gets stale data. Fix: session tokens or client-side stickiness.
- **Last-write-wins clock skew** — clocks drift between nodes, "later" write is actually earlier in wall-clock; user data silently disappears.
- **Minority writes accepted then rolled back** — in some misconfigured systems the minority side accepts writes that are dropped on heal. Properly CP systems should not do this.

## Why

### Why it exists

CAP exists because the asynchronous network model — the one that matches real-world IP networks — admits message loss, and a system that replicates state across nodes cannot tell the difference between "the other side is slow" and "the other side is unreachable". Given that ambiguity, any node holding a request must either answer locally (risking staleness) or wait (risking unavailability). The theorem is the formal statement of that fork in the road.

### Why it looks the way it does

The obvious alternative design — "just don't allow partitions" — is what single-master systems with a hot standby attempted. It works until the cross-AZ link flaps for 200ms, at which point you either failed over erroneously (split brain) or you didn't fail over and the master is gone. At any non-trivial scale, partitions are a frequent operational reality, not a rare disaster. The theorem reflects that engineering truth: you do not get to opt out of P.

A second alternative — assume a synchronous network with bounded message delay — would let you build CA systems. Some hardware-isolated, single-rack clusters approximate this. But you've moved the problem rather than solved it: the bound is now part of your failure model, and exceeding it produces incorrect behaviour rather than degraded behaviour. Most operators prefer the latter.

The "CA" label that lingers in textbook tables is misleading. A single-node database is trivially CA because the only partition is one that takes the system down entirely. Calling it CA tells you nothing useful about its distributed behaviour, because it has none.

CAP's C is **linearizability**, not the C in ACID (which is "the database stays in a valid state per its constraints") and not "eventual consistency" (which is what AP systems offer). Marketing copy routinely conflates these. When a vendor says "strongly consistent", ask whether they mean linearizable, or merely something stronger than the ridiculously weak default.

### Why it matters now

In 2026 most production systems are distributed by default — even a "single" Postgres in RDS is replicated to a standby, and most teams run multi-region. Spanner-class systems and CockroachDB have made CP at planetary scale a real option, partly by engineering partitions to be so rare that the availability cost is dwarfed by the network's own outage rate. CRDTs have moved from research curiosity to production reality in collaborative editors (Figma, Linear, Notion's local-first work). The interesting design space has shifted from "CP or AP" to "what consistency level per operation, and what is the latency cost in the no-partition case" — which is exactly what PACELC is for. CAP remains the entry point because it gives you the vocabulary to refuse the marketing claim that a system is somehow both linearizable and always available.

## Open questions / things to verify in practice

- For your candidate database, what *exactly* happens to in-flight writes when a network partition isolates the leader? Does it return errors, hang, or accept and later roll back?
- What is the default consistency level on read paths? In particular, does the system default to eventually consistent reads even when you "asked for" strong (e.g. DynamoDB, secondary indexes)?
- How long does leader election take after a failure? This is the *real* availability budget of a CP system, often a few seconds.
- Does the system tolerate clock skew, or does it (like Spanner) require a bounded time uncertainty?
- For an AP system: what is the conflict-resolution strategy, and does it silently lose writes (LWW) or merge them (CRDT)?
- Run a Jepsen-style test yourself with `iptables` partition rules and observe what the system actually does — versus what the docs claim.

Sources:
- [CAP theorem - Wikipedia](https://en.wikipedia.org/wiki/CAP_theorem)
- [Perspectives on the CAP Theorem - Gilbert & Lynch](https://groups.csail.mit.edu/tds/papers/Gilbert/Brewer2.pdf)
- [PACELC theorem - Wikipedia](https://en.wikipedia.org/wiki/PACELC_theorem)
- [Inside Cloud Spanner and the CAP Theorem - Google Cloud Blog](https://cloud.google.com/blog/products/databases/inside-cloud-spanner-and-the-cap-theorem)
- [Limits of the CAP theorem - CockroachDB Blog](https://www.cockroachlabs.com/blog/limits-of-the-cap-theorem/)
- [DynamoDB read consistency - AWS docs](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.ReadConsistency.html)
- [Cassandra Consistency Levels - AxonOps](https://axonops.com/docs/data-platforms/cassandra/architecture/distributed-data/consistency/)
- [Conflict-free replicated data type - Wikipedia](https://en.wikipedia.org/wiki/Conflict-free_replicated_data_type)
- [In Search of an Understandable Consensus Algorithm - Raft paper](https://raft.github.io/raft.pdf)
- [An Illustrated Proof of the CAP Theorem - Michael Whittaker](https://mwhittaker.github.io/blog/an_illustrated_proof_of_the_cap_theorem/)
