# Eventual Consistency — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Eventual consistency is a **liveness guarantee** on a replicated data store: if no new updates are made to a given data item, then *eventually* all replicas will return the last updated value for that item. It is deliberately weaker than **linearizability** (every read sees the most recent completed write, globally) and **sequential consistency** (operations appear in a single total order consistent with each client's program order). It says nothing about *when* convergence happens — only that, in the absence of further writes and permanent partitions, it will.

Werner Vogels' 2008 formulation (ACM Queue, "Eventually Consistent") is the reference wording. Formally, in the CAP framing (Gilbert & Lynch, 2002), an AP system exposes eventual consistency: it stays Available and Partition-tolerant, giving up the C of linearizable Consistency.

### The core building blocks

- **Replicas** — N physical copies of the same key/partition on N nodes. N is the *replication factor*.
- **Replication protocol** — the mechanism that ships a write from the node that accepted it to the others. Usually asynchronous, sometimes with a small synchronous prefix (e.g. wait for W acks before returning).
- **Anti-entropy** — background repair (Merkle-tree diffing, read repair, hinted handoff) that fixes divergence the foreground path missed.
- **Gossip / membership protocol** — how nodes learn who else is alive, which ranges each owns, and how to route requests. Cassandra and Dynamo both use SWIM-style gossip.
- **Causality tracking** — vector clocks, version vectors, or dotted version vectors that let a replica tell "this update happened *after* that one" from "these two updates happened *concurrently*".
- **Conflict resolution** — the deterministic function applied when two replicas disagree: last-write-wins (LWW), sibling exposure to the application, or a CRDT merge.
- **Session guarantees** — the four client-scoped guarantees from Terry et al.'s 1994 Bayou paper: read-your-writes, monotonic reads, monotonic writes, writes-follow-reads.

### How it relates to the broader landscape

Eventual consistency sits at the weak end of a spectrum whose strong end is linearizability. Between them live **causal consistency** (concurrent writes may diverge but causally related ones are ordered) and **read-your-writes / bounded staleness** (weak-but-not-arbitrary). Sibling models — **strong eventual consistency** (SEC), the guarantee CRDTs give — add that replicas which received the same set of updates are in the same state, independent of delivery order. PACELC (Abadi, 2010) is the framework that makes eventual consistency an *always-on* trade-off: Partition → A vs C; Else → Latency vs Consistency. Eventually-consistent systems are almost always PA/EL.

## Where

### Where it runs / lives in the stack

Almost always at the **data layer**: replicated key-value stores, wide-column stores, object stores, DNS, and CDN caches. It surfaces upward as a *contract* the storage engine exposes to application code. Above the data layer, application frameworks may add convergence patterns (event sourcing, outbox, CRDT libraries) that make eventual consistency tractable for domain state, not just infrastructure state.

### Where you typically encounter it

- **Amazon DynamoDB** — reads are eventually consistent by default; `ConsistentRead=true` costs 2× RCUs and pins you to the leader replica in the region.
- **Apache Cassandra / ScyllaDB** — tunable per-request via `CONSISTENCY LEVEL` (ONE, QUORUM, ALL, LOCAL_QUORUM, EACH_QUORUM).
- **Amazon S3** — since Dec 2020, S3 offers strong read-after-write for new objects; historically it was eventually consistent on overwrites and deletes.
- **DNS** — the canonical eventually-consistent system. TTL drives convergence latency.
- **Git** — every clone is a replica; merge is your conflict resolution function.
- **CDN edges, Redis replicas, MongoDB secondaries** — all give stale reads by default unless you opt into read-from-primary or `readConcern: "linearizable"`.

### Ecosystem and tooling

- **For key-value / wide-column at scale**: DynamoDB, Cassandra, ScyllaDB, Riak (retired but historically important), Azure Cosmos DB (five configurable consistency levels).
- **For collaborative / offline-first apps**: Yjs, Automerge, Y-sweet, Liveblocks — CRDT libraries and hosted sync.
- **For service replication**: etcd and ZooKeeper are the *counterexamples* (linearizable via Raft/ZAB) — they're what you use when eventual consistency is not enough.
- **For anti-entropy operations**: Cassandra's `nodetool repair` (full, incremental, sub-range), DynamoDB's internal Merkle-tree reconciliation (hidden from users).
- **For causal tracking research/production**: interval tree clocks, dotted version vectors (Riak 2.x), TSO-based hybrid logical clocks (CockroachDB uses HLC for a different, stronger model).

## When

### When the topic emerged and why

The idea is older than "NoSQL". The Bayou project at Xerox PARC (1994–1996) built a mobile database for disconnected laptops and formalized session guarantees. DNS (RFC 1034, 1987) baked eventual consistency into the internet's naming layer because global strong consistency for hostname lookups is a non-starter. The 2007 Dynamo paper crystallized the modern packaging — sloppy quorums, vector clocks, Merkle-tree anti-entropy, hinted handoff — and launched a decade of Dynamo-inspired stores (Cassandra, Riak, Voldemort). The motivator throughout: Amazon-scale shopping carts and social feeds cannot afford the round-trip latency (or availability hit) of a global consensus per write.

### When to use it in a project

Reach for it when:

- Your write path must survive a regional outage or a cross-region link flap without going read-only.
- P99 write latency has a budget under ~50 ms and your replicas span more than one datacenter.
- Reads dominate writes by an order of magnitude and staleness measured in seconds is acceptable to the domain.
- The data is naturally mergeable (counters, sets, feeds, presence, telemetry, shopping carts) or has a clear tiebreak (LWW is acceptable).
- You can layer a **stronger** model (session guarantees, causal, or per-key linearizable via compare-and-set) on the *few* records that need it, and let the rest ride the weak default.

### When NOT to use it

Avoid it when:

- The domain has a **non-negotiable invariant** across concurrent writers: uniqueness, non-negative balance, finite inventory, monotonic counters that must never lose an increment (LWW *will* lose one).
- Users experience the storage directly and expect read-your-writes with zero glue (a single-region Postgres will make you happier and cheaper).
- Your team lacks operational tooling to observe divergence: no metric for replica lag, no dashboard for repair progress, no procedure for conflict rate. Silent divergence in production is the classic failure.
- Regulatory or audit requirements demand a linear, timestamped total order.

## How

### How it works under the hood

Trace a write through a Dynamo-style store with N=3, W=2, R=2:

1. **Client → coordinator.** Client sends `PUT(k, v)` to any node. That node (chosen by consistent hashing on `k`) becomes the coordinator for this request.
2. **Coordinator fans out.** It forwards the write to the N=3 replicas responsible for `k`'s hash range (the preference list).
3. **Quorum ack.** The coordinator waits for W=2 acks (including itself) and returns success to the client. The third replica is still catching up.
4. **Versioning.** Each write is stamped with a version — a vector clock in classic Dynamo, a timestamp in Cassandra (LWW by design), a dotted version vector in Riak 2.
5. **Hinted handoff.** If a target replica is temporarily unreachable, another node writes a "hint" — a stored intent to be delivered when the target returns. This preserves W availability during short partitions.
6. **Read path.** A `GET(k)` fans out to R=2 replicas. Because W+R > N (2+2 > 3), the read set and write set overlap on at least one replica that saw the latest write. The coordinator picks the version with the highest vector clock (or highest timestamp, or exposes siblings if concurrent).
7. **Read repair.** If replicas disagreed, the coordinator writes the winning version back to the stale replicas synchronously (foreground repair) or asynchronously (background repair, sampled).
8. **Anti-entropy.** Periodically each replica pair builds a Merkle tree of its data ranges, exchanges root hashes, and drills down only into subtrees whose hashes differ. This bounds bandwidth: O(log n) comparisons for a single differing key, not O(n).
9. **Gossip.** Membership, load, and schema flow through a SWIM-like gossip protocol so any node can route to any key without a central directory.

Convergence guarantee: once writes stop and partitions heal, hinted handoff drains, read repair fires on the next access, and anti-entropy sweeps the cold data. The system reaches a single value per key.

### Key trade-offs

| Design choice | Gained | Given up |
|---|---|---|
| Async replication + W < N | Low write latency, availability under node failure | Stale reads until propagation completes |
| Quorum with R+W > N | Overlap guarantees latest value is *visible*, not just *present* | Read cost scales with R; writes cost W |
| LWW conflict resolution | Zero application code, deterministic | Silently drops concurrent updates; clock skew becomes a data-loss vector |
| Vector clocks / siblings | No lost updates; app can merge | Client complexity, unbounded metadata growth (Dynamo truncates oldest entries) |
| CRDTs | Automatic, mathematically sound merge (strong eventual consistency) | Restricted data model; state size can grow (tombstones, causal history) |
| Session guarantees | Users see their own writes | Client-side session tokens or sticky routing required |
| Tunable per-request (Cassandra) | Same cluster serves strict and lax workloads | Operators must reason per-query; wrong CL is a foot-gun |

### Common failure modes

- **Clock skew under LWW** — a node with a fast clock overwrites correct data written milliseconds later on a slow node. Mitigation: NTP discipline, HLC, or move off LWW.
- **Sibling explosion** — pathological client code repeatedly writes without reading first; vector clocks fork endlessly. Cassandra sidesteps this by refusing sibling semantics; Riak exposes it and it bites.
- **Sloppy quorum masking divergence** — writes accepted by "any N nodes" (not necessarily the preference list) during a partition can leave the canonical replicas cold. Anti-entropy must fix it, and until then reads may miss the write even at QUORUM.
- **Repair storms** — full `nodetool repair` on a large cluster saturates disk and network; incremental repair with anti-compaction bugs (CASSANDRA-9143, historically) corrupted SSTables. Modern practice is sub-range incremental with a scheduler like Reaper.
- **Read-your-writes violation on GSIs** — DynamoDB GSIs are always eventually consistent; a user who writes to the base table and immediately queries the GSI sees old data. Classic "why is my UI wrong" incident.
- **Tombstone accumulation** — deletes in Cassandra are tombstones; if `gc_grace_seconds` (default 10 days) passes without a repair, deleted data can resurrect. Every eventually consistent store has some variant of this "delete is hard" problem.

## Why

### Why it exists

At its root, eventual consistency exists because **the speed of light and the reliability of networks are not free**. A synchronous global replication protocol pays a coordination round-trip on every write — tens of milliseconds cross-region, hundreds cross-continent — and it stalls entirely during a partition. For workloads where a few seconds of staleness is fine but a few seconds of downtime is not, the sane trade is to accept temporary divergence and repair in the background. It is CAP's forced choice made deliberately, plus PACELC's admission that even outside partitions, latency and consistency are in tension every millisecond.

### Why it looks the way it does

The obvious alternative — synchronous multi-primary replication with distributed locks — was tried (early Oracle RAC, Sybase Replication Server) and repeatedly failed at internet scale: locks amplify tail latency, deadlocks compound across regions, and any coordinator failure stalls everything. Consensus (Paxos, Raft) is the modern strong-consistency answer, and it works — Spanner, CockroachDB, etcd all use it — but consensus fundamentally costs a quorum round-trip per write and cannot serve writes during a majority partition. Eventual consistency's design is the *inverse*: local writes are always allowed, and correctness is reconstructed after the fact via causality metadata and background repair. Vector clocks over Lamport timestamps, quorums over primary-only, gossip over centralized directories — every choice is picked to eliminate coordination on the hot path. CRDTs are the mathematical maturation of this design: instead of resolving conflicts after they occur, define your data types so conflicts are algebraically impossible.

### Why it matters now

In 2026, three forces keep this topic central. First, **multi-region is the default** for anything user-facing above ~10 rps: even startups launch on globally-distributed platforms (DynamoDB Global Tables, Cosmos DB, Cloudflare D1, Turso), and every one of them exposes eventual consistency somewhere. Second, **local-first and offline-first software** — Automerge, Yjs, Liveblocks, Linear's sync engine, Figma's multiplayer — has moved CRDTs from research curiosity to shipping product; every collaborative editor is now an eventually consistent database in disguise. Third, AI workloads (vector search, feature stores, agent memory) tolerate staleness cheaply and reward the latency win; expect more AP-flavored stores optimized for read scale over write recency. Meanwhile DynamoDB's Dec 2024 preview of multi-region *strong* consistency in Global Tables signals the opposite pressure — customers want the option to escape eventual consistency for the hot 5% of their data while keeping the cheap defaults for the other 95%. The tunable model is winning.

## Open questions / things to verify in practice

- What is my system's actual convergence latency P50 and P99, measured end-to-end, not just replication lag? (Instrument a write-then-poll canary.)
- What conflict rate am I seeing per key per day? If zero, LWW is probably fine; if non-zero, LWW is silently losing data.
- Do my client SDKs give me sticky session routing so read-your-writes actually holds, or does load balancing scatter reads across replicas?
- When my anti-entropy repair runs, how much CPU, disk, and network does it consume, and does it complete inside the `gc_grace_seconds` window?
- If I set W=1 and R=1 for latency, can I state — with a metric — the staleness budget I'm exposing to users, or am I flying blind?
- Which of my "eventually consistent" writes actually need to be causal or linearizable, and can I isolate those to a tiny set of keys served by a stronger primitive (a Raft group, a conditional put, or a CAS)?

Sources consulted during writing include Vogels' "Eventually Consistent" (ACM Queue 2008), the Dynamo paper (SOSP 2007), Terry et al.'s Bayou session-guarantees paper (PDIS 1994), Shapiro et al.'s CRDT paper (INRIA 2011), Abadi's PACELC note (2010), and the current Cassandra and DynamoDB documentation.
