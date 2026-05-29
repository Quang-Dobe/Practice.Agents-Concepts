# CAP Theorem — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

Every design review for a multi-region service eventually walks into the CAP question, even when nobody names it. The practical form is blunt: *during a partition, do we prefer to refuse the request or risk diverging?* That single question shapes the database choice, the API contract, the on-call runbook, and the post-mortem template.

You will meet it when you pick the store behind a payments ledger, when you decide whether your shopping cart should accept writes from a region that has lost contact with the primary, when you argue with a vendor about what "strongly consistent" means on their box, and when an inter-AZ link flaps for 90 seconds and your dashboards light up.

The trap is treating CAP as a one-time static decision ("we are a CP shop"). In real systems the trade-off is made per endpoint, per table, sometimes per request, and the production pain comes from endpoints whose consistency contract was never written down.

## Best practices

### 1. Frame every replicated endpoint with an explicit partition contract
**Do:** Decide, per endpoint, what happens during a partition — *serve stale*, *queue and ack*, or *refuse with a typed error*. Write it in the API doc next to the request schema.
**Why:** Without this, behavior under partition is whatever the underlying driver's defaults are, which surprises everyone at 3am. A typed `409 PartitionedWrite` is a feature; an HTTP 500 followed by a silent retry is an incident.
**Avoid:** "We'll deal with it when it happens." You will, and badly.

### 2. Make the consistency level visible at the API boundary
**Do:** Surface read consistency in the request — e.g. `?consistency=strong` on a critical balance read, default eventual elsewhere. DynamoDB models this well with `ConsistentRead` per request; Mongo via `readConcern: "majority"`.
**Why:** Callers downstream of you make correctness assumptions. If the consistency choice is buried in a config file three layers below, nobody knows whether the balance they just read is authoritative.
**Avoid:** A single global default that quietly forces every read through the slowest, costliest path — or worse, the cheapest, stalest one.

### 3. Use leader-based CP for anything that resembles a ledger or a lock
**Do:** Reach for etcd, ZooKeeper, Spanner, CockroachDB, or a Raft-backed service for leader election, configuration, distributed locks, financial state, and inventory decrement.
**Why:** These workloads have a single-source-of-truth requirement at write time. Eventual convergence is not acceptable when the failure mode is "we sold the same seat twice." The latency cost (a majority round-trip) is the price of correctness.
**Avoid:** Implementing your own "leader" on top of Redis pub/sub or a Postgres advisory lock and hoping the partition behavior works out. Redlock has well-documented correctness debates; treat it accordingly.

### 4. Default to `LOCAL_QUORUM` for multi-region Dynamo-style stores
**Do:** In Cassandra, ScyllaDB, or DynamoDB Global Tables, default reads and writes to a quorum **inside one region**, not across regions. Reserve `EACH_QUORUM` (cross-region) for the small set of operations that genuinely need it.
**Why:** Cross-region quorum buys you very little on a healthy network and turns a single regional brownout into a global outage. Cassandra's default consistency for `cqlsh` is `ONE`, which is AP-leaning and the wrong default for most application code — set it explicitly.
**Avoid:** Leaving driver defaults in place and discovering on incident day that "quorum" meant "any one replica."

### 5. Pick conflict resolution on purpose
**Do:** For AP write paths, decide between last-write-wins, vector clocks, CRDTs, or a domain-specific merge function *before* shipping. Document which writes can converge by merge (set membership, presence, counters with G-Counter semantics) and which cannot (account balance, inventory level).
**Why:** Last-write-wins is a decision, not a default — it silently loses data on concurrent writes, and clock skew makes "last" a fiction. Concurrent edit bugs in collaborative tools and "my cart item vanished" reports are almost always LWW.
**Avoid:** Adopting Cassandra or DynamoDB and assuming concurrent writes "just work." They merge by timestamp, which is LWW under a friendlier name.

### 6. Use idempotency keys end-to-end on any AP write path
**Do:** Every mutating request carries a client-generated idempotency key; the server stores `(key -> result)` for long enough to cover any reasonable retry window (Stripe uses 24h).
**Why:** AP systems encourage clients to retry on timeout. Without an idempotency key, retries during a partition produce duplicate charges, duplicate orders, duplicate emails. The CAP choice does not absolve you of exactly-once *effects* at the application boundary.
**Avoid:** Relying on the database's own dedup. It usually does not exist at the granularity you need.

### 7. Design degradation modes, not just success paths
**Do:** For each user-facing flow, pre-decide one of three behaviors when the store is partitioned: (a) serve stale data with a freshness header, (b) accept writes into a durable queue and ack with a "pending" status, (c) refuse with a clear error. Document and test all three.
**Why:** Steady-state engineering is easy; partition-state engineering is what separates a 4-nines service from a 2-nines one. Teams that have not pre-decided default to "5xx everything," which converts a degraded experience into a full outage.
**Avoid:** Generic retries with exponential backoff as your only partition strategy. They mask the problem until the queue collapses.

### 8. Test partition behavior on purpose
**Do:** Run Jepsen-style fault injection, or at minimum `tc netem` / Toxiproxy in CI, dropping inter-AZ traffic for 30–120 seconds. Verify that writes either succeed and stick, fail cleanly, or are rolled back as documented.
**Why:** Every major distributed database has been embarrassed by Jepsen at some point. Your application code's behavior under partition is almost certainly worse than the database's, and you will not find out otherwise until production.
**Avoid:** Trusting vendor consistency claims without testing them yourself. "Linearizable" in marketing is sometimes "linearizable if the moon is full."

### 9. Reason in PACELC, not just CAP, for steady-state decisions
**Do:** When picking between two CP systems or two AP systems, use the *else* half of PACELC — *Else, Latency or Consistency?* — because partitions are rare and steady-state latency is what you pay for daily.
**Why:** CAP's binary buckets group Spanner and ZooKeeper together; PACELC distinguishes them on the dimension that matters at p99 every minute of the day. Most of the operational pain budget is spent here, not on partitions.
**Avoid:** Picking a strongly-consistent global store for a workload that could tolerate causal consistency and then complaining about cross-region write latency you signed up for.

### 10. Match the data to the trade — money to CP, social feeds to AP
**Do:** Ledgers, inventory, seat reservations, leader election, config -> CP. Shopping carts, social feeds, presence, view counters, recommendation state -> AP, ideally CRDT-friendly.
**Why:** A shopping cart that 5xx's during a partition loses revenue and trust; a transient inventory inconsistency that oversells one SKU is a refund and an apology. Pick the failure mode whose cost is *lower than the cost of the alternative*.
**Avoid:** The reverse — AP for money (silent overdrafts), CP for newsfeeds (down on every regional blip).

## Anti-patterns to recognize

- **"We're a CA system"**: The team has decided partitions don't happen on their network. They still happen; the system just degrades in undefined ways. Better alternative: pick CP or AP explicitly and own the degradation.
- **Conflating CAP-C with ACID-C**: A team argues a single-node Postgres is "CP because ACID." ACID-C means constraints hold; CAP-C means linearizability across replicas. They are unrelated. Better alternative: keep the two vocabularies separate in design docs.
- **"Eventually consistent" used as a synonym for "consistent"**: A vendor claim, or worse an internal one, that papers over the fact that reads can return arbitrarily stale data. Better alternative: name the actual consistency model — read-your-writes, monotonic reads, causal, linearizable.
- **Read-modify-write on top of Cassandra without LWT**: Reading a counter, incrementing in app code, writing back. Concurrent writers silently overwrite each other. Better alternative: Cassandra lightweight transactions (Paxos on top of the AP path) for the rare strong-write case, or model the value as a counter type that converges under merge.
- **Treating CAP as the only design dimension**: Ignoring the steady-state latency-vs-consistency trade (PACELC's ELC half), which is where most of the operating cost actually lives. Better alternative: write the consistency contract *and* the p99 latency target on the same page.
- **Bolting strong consistency onto an AP system at the application layer**: Hand-rolled locks in DynamoDB using conditional writes spread across multiple items; "two-phase commit" implemented in app code over an eventual store. These break under partition in subtle ways. Better alternative: put the strongly-consistent slice on a CP store and keep the bulk on the AP store.
- **Misreading defaults**: Assuming DynamoDB reads are strongly consistent (they default to eventual and cost half as much) or that MongoDB writes are durable across the replica set (`writeConcern: 1` only waits for the primary). Better alternative: pin the consistency settings explicitly in client config and code-review them.
- **Trusting wall-clock timestamps for ordering**: LWW with NTP-synchronized clocks looks fine until a leap second or a 200ms skew silently drops a user's write. Better alternative: hybrid logical clocks, vector clocks, or a CP store for ordering-sensitive data.

## Real-world usage patterns

**Multi-region SaaS with a CP control plane and an AP data plane.** A B2B SaaS runs configuration, tenant metadata, and feature flags on etcd or CockroachDB (CP), and runs the per-tenant high-volume event stream on Cassandra or DynamoDB with `LOCAL_QUORUM` (AP). Non-obvious lesson: the CP control plane becomes the limiting factor for region failover speed — the time to elect a new Raft leader is your real RTO floor, often 5–15 seconds.

**Spanner-backed financial ledger.** A fintech runs its core ledger on Spanner, leveraging TrueTime to get external consistency across regions. Reads are linearizable by default; bulk analytics use bounded stale reads (`read_timestamp = now - 10s`) to bypass the leader. Non-obvious lesson: most reads do not need to be linearizable, and routing them to stale reads can drop p99 latency by an order of magnitude without weakening the actual correctness contract.

**Collaborative editor with CRDTs.** A document editor (Figma-style) uses CRDTs (Yjs or Automerge) for the document body and a CP store (Postgres with strict serializable transactions) for permissions and billing. Document edits converge under merge with no central authority; permission changes go through the CP store. Non-obvious lesson: even in a "fully local-first" product, the *boundary* between CRDT-land and CP-land is where 80% of the bugs live — permission revocation that races with an in-flight edit is the canonical example.

**E-commerce with Cassandra plus LWT for the hot path.** A retail platform stores cart contents and product catalog in Cassandra at `LOCAL_QUORUM` (AP-leaning, fast, multi-region-tolerant), but uses Cassandra's lightweight transactions (Paxos-backed `IF NOT EXISTS`) for inventory decrement at checkout. Non-obvious lesson: LWT is roughly 4x the latency of a normal Cassandra write — using it everywhere defeats the point of Cassandra, but using it nowhere oversells stock during a flash sale.

**Kafka as the durable buffer in front of an AP store.** A telemetry pipeline writes events to Kafka with `acks=all` and `min.insync.replicas=2` (CP-like for the durability contract, via ISR semantics), then a consumer fans out into Cassandra. Non-obvious lesson: Kafka's `unclean.leader.election.enable=true` silently flips this from CP to AP and can lose committed offsets — verify it is off in production.

## Operational checklist

- **Monitoring:** Do you alert on partition signatures (inter-AZ packet loss, leader election rate, replica lag, hinted-handoff queue depth)?
- **Failure handling:** For each replicated endpoint, is the documented partition behavior actually tested with fault injection in CI?
- **Idempotency:** Does every mutating endpoint accept a client-generated idempotency key, and is the dedup window long enough for your retry strategy?
- **Consistency defaults:** Are the database client's consistency / read-concern / write-concern settings pinned explicitly in code, not left to driver defaults?
- **Split-brain protection:** For CP systems, is N odd, are leases longer than your worst-case GC pause, and is `unclean.leader.election` (or its equivalent) off?
- **Conflict resolution:** For AP writes, is the merge strategy documented per data type, and does it avoid silent LWW where it matters?
- **Cost:** Are you paying for strong reads (e.g. DynamoDB `ConsistentRead`) on endpoints that don't need them? Cross-region quorum on endpoints that don't need them?
- **Security foot-gun:** Stale reads of authorization data — a revoked permission that takes 30 seconds to propagate. Is the auth path on a strong-read code path?
- **Runbook:** Does the on-call runbook tell the engineer what to do if the store reports "no quorum" — fail over, wait, or page someone?
- **Onboarding:** Can a new engineer answer "what happens to a write to endpoint X if us-east-1 is isolated?" on day one? If not, the contract is not really written down.

## How this topic typically evolves in a codebase

Teams usually start single-region with a managed Postgres (or equivalent) and never think about CAP. The first scaling pain is read load, solved with replicas — and the first CAP-shaped bug arrives when application code reads from a lagging replica and gets data the user just wrote. The team adds read-your-writes via sticky sessions or routing primary-after-write, often without naming it.

The second wave is multi-region. Latency forces a real choice: either run a CP global store (Spanner, CockroachDB) and pay the write-latency tax, or shard data by region and accept that cross-region reads are eventual. Most teams pick the second path because it is cheaper, then spend the next two years building application-level logic to handle the cases where data crosses regional boundaries (a user travels, a tenant has employees in two regions, a shared resource is contended). This is the painful migration point: the CAP choice that was made implicitly per table now needs to be made explicitly per endpoint, and undoing it usually means a long dual-write period.

The mature state is hybrid: a small CP core (identity, billing, leader election) on a strongly-consistent store; a large AP body (events, content, social state) on a Dynamo-style store with documented per-endpoint consistency contracts; and a thin layer of CRDTs or domain-specific merge logic for the data that genuinely needs offline-tolerant collaboration. Teams that get here usually wish they had written the partition contract down on day one.

## Further reading

- [Please stop calling databases CP or AP — Martin Kleppmann](https://martin.kleppmann.com/2015/05/11/please-stop-calling-databases-cp-or-ap.html) — Why the static labels mislead, from the author of *Designing Data-Intensive Applications*.
- [Jepsen analyses](https://jepsen.io/analyses) — Kyle Kingsbury's empirical takedowns of distributed databases under partition. Read the one for whatever you are about to deploy.
- [Inside Cloud Spanner and the CAP Theorem — Eric Brewer](https://cloud.google.com/blog/products/databases/inside-cloud-spanner-and-the-cap-theorem) — Brewer himself on why Spanner is "effectively CA" in practice, with honest caveats.
- [Consistency Tradeoffs in Modern Distributed Database System Design (PACELC) — Daniel Abadi](https://www.cs.umd.edu/~abadi/papers/abadi-pacelc.pdf) — The paper that adds the missing latency-vs-consistency axis CAP omits.
- [Designing Data-Intensive Applications — Martin Kleppmann](https://dataintensive.net/) — Chapter 5 (Replication) and Chapter 9 (Consistency and Consensus) are the canonical practitioner's reference.
- [A Critique of the CAP Theorem — Martin Kleppmann](https://arxiv.org/abs/1509.05393) — The case that CAP is too coarse to be useful as a design tool, and what to use instead.
