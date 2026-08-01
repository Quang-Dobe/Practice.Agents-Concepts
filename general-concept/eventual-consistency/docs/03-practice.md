# Eventual Consistency — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, eventual consistency is what quietly sits under every feature that spans more than one region, every read replica pool fronting a busy primary, every cache in front of a database, every "activity feed" or "notification center" that hydrates from a projection. You do not opt into it — it opts into you the moment your architecture diagram has more than one box holding the same data.

In a mobile or web product, it is what the UI has to hide when the user hits Save and the write goes to `us-east-1` but the follow-up read hits a replica in `eu-west-1` that has not caught up. In an e-commerce backend, it is the reason "your order was placed" screens show optimistic UI first and reconcile with the truth 200 ms later. In a collaborative editor (Figma, Linear, Notion), it is the whole game: every client is a replica, every keystroke is a write, and the sync engine's job is convergence.

You will most often meet it running on top of DynamoDB, Cassandra/ScyllaDB, Cosmos DB, Redis with replicas, Postgres/MySQL with read replicas, S3 (for overwrites/deletes historically), Elasticsearch, or any CRDT library shipping in a client-side sync engine.

## Best practices

### 1. Make every write idempotent, with a client-supplied key
**Do:** Attach a client-generated `idempotency_key` (UUID or hash of the intent) to every mutating request. Store it server-side with the result. Retry paths dedupe against it.
**Why:** In an eventually consistent world, retries are constant — network flaps, hinted handoff replays, at-least-once queues, client reconnects. Non-idempotent writes turn transient network glitches into duplicated charges and double-posted comments.
**Avoid:** Relying on the database's auto-increment ID or the request's arrival timestamp as the dedupe key. Both differ across replicas and retries.

### 2. Provide read-your-writes with sticky routing or a "write echo"
**Do:** After a write, pin the client's next N reads to the primary/leader (session pinning on the load balancer, or a short-lived `session_token` that encodes the write's LSN). Alternatively, return the newly-written value in the write response and let the client patch its local cache.
**Why:** The number one "why is production weird" ticket in eventually consistent systems is a user who submits a form, gets redirected, and sees the old data. Users interpret this as data loss, even when the write succeeded.
**Avoid:** Trusting a round-robin load balancer to accidentally do the right thing. It won't, and the bug reproduces once in ten and never on your laptop.

### 3. Pick your quorum so R + W > N, and know what "N" really is
**Do:** For classic Dynamo-style stores, set W and R such that W + R > N (e.g. N=3, W=2, R=2). For multi-region, use `LOCAL_QUORUM` in Cassandra by default and only step up to `EACH_QUORUM` when cross-region agreement is a hard requirement.
**Why:** R + W > N guarantees a read set overlaps a write set on at least one replica that saw the latest write. Getting this wrong is how you ship a "strongly consistent" feature that silently returns stale data under normal load.
**Avoid:** W=ALL for "safety" — one dead replica takes writes down. W=1 R=1 for "speed" — you have no consistency guarantees at all and no way to detect it in a dashboard.

### 4. Do not use LWW for data you cannot afford to silently lose
**Do:** Use LWW only for data where "the newest write wins" is the actual business rule (user profile fields, last-seen timestamps, cache entries). For counters use CRDT counters (`PN-Counter`) or atomic increments. For sets use OR-Sets or server-side `ADD`/`REMOVE`. For structured docs consider Automerge/Yjs.
**Why:** LWW under concurrent writes drops the loser silently — no exception, no log line, no metric. The classic incident: two devices edit a note, one wins, the other's paragraph vanishes overnight. Clock skew (even single-digit ms) makes this non-deterministic.
**Avoid:** "We'll just use LWW and revisit if it becomes a problem." You will not notice; the losing writes are gone by the time anyone looks.

### 5. Enforce hard invariants at a single serialization point
**Do:** For uniqueness (usernames, email), non-negative balance, finite inventory, or "at most one winner" auctions, use a strongly consistent primitive: a conditional write (`ConditionExpression` in DynamoDB, `IF NOT EXISTS` in Cassandra LWT, a Postgres unique index, a Redis `SET NX`, or a small Raft group). Do this even if the rest of the system is eventually consistent.
**Why:** Eventual consistency plus concurrent writers cannot preserve global invariants. You will oversell inventory during Black Friday, and the reconciliation will be manual.
**Avoid:** "Check then write" application code across replicas. The check is stale by the time the write lands.

### 6. Measure convergence and divergence as first-class metrics
**Do:** Emit replication-lag P50/P99 per region, read-repair rate per key range, conflict/sibling rate per table, hinted-handoff queue depth, and — most importantly — a synthetic "write here, read there, measure delta" canary. Alert on the delta exceeding your stated staleness budget.
**Why:** Silent divergence is the classic failure mode. You will not find out from users; you will find out from an auditor. Without a canary you literally cannot answer "how stale is my data right now?"
**Avoid:** Treating replication lag as an infra-team dashboard only. Product engineers need it to reason about UX.

### 7. Design UI to tolerate and expose staleness
**Do:** Use optimistic UI for writes (show the new state immediately, roll back on failure). Show "last updated 3s ago" on list views. On critical reads, add a manual refresh with a visible spinner rather than pretending the data is live.
**Why:** Users tolerate delay if it is visible. They panic at silently wrong data. The UX layer is where "eventual" becomes acceptable or scandalous.
**Avoid:** Blocking the UI until the read replica catches up. That defeats the entire reason you chose an AP system.

### 8. Route writes and reads to the closest healthy replica, but tag reads with a bound
**Do:** In DynamoDB, use eventual reads by default (half the RCU cost) and reserve `ConsistentRead=true` for the ~5% of reads that need it. In Cassandra, default to `LOCAL_ONE` for warm-path reads and `LOCAL_QUORUM` for anything that must be right.
**Why:** Consistent reads cost 2x the money and 2x the latency for a guarantee most of your reads do not need. Blanket-applying the strongest level is how the AWS bill doubles overnight.
**Avoid:** One consistency level for the whole application. Almost every workload is heterogeneous.

### 9. Contain conflict domains — one CRDT per document, not per system
**Do:** Model each independently-editable unit (a document, a card, an issue) as its own CRDT or its own conflict-resolution scope. Keep cross-document invariants outside the CRDT, enforced by a coordinator.
**Why:** CRDTs scale merge cost with the number of concurrent writers on the *same* object. A global CRDT for the whole app is a metadata bomb (unbounded tombstones, vector-clock growth).
**Avoid:** "Everything is a Yjs doc." You will hit memory limits and pathological merge times within a year.

### 10. Plan the anti-entropy schedule before you need it
**Do:** Schedule Cassandra `nodetool repair` sub-range and incremental, run inside `gc_grace_seconds` (default 10 days), and use a scheduler (Cassandra Reaper) rather than cron. For DynamoDB, trust the internal reconciliation but monitor `SystemErrors` and cross-region `ReplicationLatency`.
**Why:** Skipping repair past `gc_grace_seconds` resurrects deleted rows (zombie data). Repair storms during business hours saturate disk and page on-call.
**Avoid:** "We'll add repair when it becomes a problem." When it becomes a problem, it is already a data-corruption incident.

## Anti-patterns to recognize

- **Read-modify-write across replicas.** Fetching a counter, adding one, writing it back — from an eventually consistent read — silently drops concurrent increments. Use atomic `ADD` or a CRDT counter instead.
- **Client-clock LWW.** Letting the *client* stamp writes with `Date.now()` means a laptop with a wrong clock overwrites everyone's correct data. Use server time, HLC, or vector clocks.
- **"Cache-aside" without invalidation ordering.** Writing to the DB, then deleting the cache — with a concurrent reader who reads-then-writes-the-cache in between — leaves stale data in cache forever. Use write-through, or invalidate before write, or accept a TTL.
- **Cross-partition transactions in a wide-column store.** Cassandra batch across partitions is not a transaction; it is a coordinator convenience with worse durability. If you need atomicity across keys, you picked the wrong database.
- **Trusting GSI/secondary index for read-your-writes.** DynamoDB GSIs and Elasticsearch indexes update asynchronously from the source of truth. A user who writes and immediately queries the index sees old data. Route the immediate-read through the base table, or accept the delay in UX.
- **"Strong consistency" via double reads.** Reading twice and comparing does not give you consistency; it gives you two stale reads. There is no client-side trick that manufactures a stronger guarantee than the store provides.
- **Silent conflict resolution buried in ORMs.** Some ORMs (and some Redis clients) hide `SET` semantics behind an `.update()` method that quietly replaces the object. You lose fields that another writer set. Read the driver's conflict behavior before shipping.

## Real-world usage patterns

**Global product catalog, read-heavy SaaS.** A B2B analytics product ships catalog metadata to a DynamoDB Global Table across three regions. Writes go to a single "authoring" region; every other region serves eventual reads. The non-obvious lesson: they added a `version` field and a client-side "if you see version < X, refetch after 500 ms" fallback, because the 1–5 s cross-region lag was visible to admins editing their own tenant. Eventual consistency was correct, but the humans doing the writing needed read-your-writes glue on top.

**Multiplayer design tool (Figma-style).** Every client holds a local copy of the document tree. Edits are applied optimistically, sent to a per-document server that assigns a monotonic sequence number and rebroadcasts. Conflict resolution is *property-level LWW*, not full CRDT — because in a design tool, two people rarely edit the same property of the same object at the same millisecond. The lesson: pick the *weakest* conflict resolution that still preserves your product's invariants. Full CRDT metadata was unnecessary and expensive.

**Payments ledger with an eventually consistent read model.** The write side (double-entry journal) is a single strongly-consistent Postgres primary — no room for eventual anything. The read side (customer-facing balance UI, reports) is a Kafka-fed projection served from a replica with 1–3 s lag. The lesson: split "the source of truth" (strong) from "the view" (eventual). Do not try to unify them.

**IoT telemetry ingest.** Millions of devices post metrics into a Cassandra cluster with `CONSISTENCY LEVEL = LOCAL_ONE` writes and `LOCAL_QUORUM` reads for dashboards. Losing a single metric point is fine; the aggregate is what matters. The lesson: when the data is statistical, eventual consistency is not a compromise — it is the natural fit, and stronger consistency would waste money for no user-visible benefit.

## Operational checklist

- Monitoring: Is replication lag (per region, per replica) alerted on with a threshold tied to your product's stated staleness SLA?
- Monitoring: Is there a synthetic write-then-read-elsewhere canary running continuously with P50/P99 dashboards?
- Failure handling: What happens during a regional partition — can writes continue in each region, and is the merge/conflict path tested (chaos-tested, not just documented)?
- Failure handling: If a hint-carrying node dies, is data loss bounded and observable?
- Security/correctness: Are all mutating endpoints idempotent with a client-supplied key, and is the dedupe store's TTL longer than the client's retry budget?
- Correctness: For every business invariant (uniqueness, non-negative balance, inventory), is there a single serialization point enforcing it — not application-level "check then write"?
- Cost: Are `ConsistentRead=true` (DynamoDB) or `QUORUM`+ (Cassandra) reads audited? Do you know what fraction of RCU/CPU they consume?
- Cost: Does anti-entropy repair run inside its window without saturating disk during business hours?
- Onboarding: Can a new engineer point to the doc that says "here is our staleness budget, here is which tables are strong vs eventual, here is why"?
- Onboarding: Is there a runbook for "user reports stale data" that starts with lag dashboards, not with random cache flushes?

## How this topic typically evolves in a codebase

Teams almost always start on the strong-consistency end without noticing — one Postgres, one region, no replicas. Everything Just Works. The first crack appears when a read replica is added for scale: suddenly the app has a subtle read-after-write bug on the profile page and someone hacks in a "read from primary for 5 seconds after write" workaround. That workaround is the first eventually-consistent surface, and it usually lives forever.

The second phase arrives with multi-region or with a move to DynamoDB/Cassandra. Now eventual consistency is the default, not the exception, and the team either (a) discovers this in an incident and retrofits idempotency keys, canaries, and session pinning under duress, or (b) plans for it up front. Option (a) is more common. The painful migration point is usually the day a well-meaning refactor moves a "check then write" invariant across a partition boundary and the resulting duplicates take a week to reconcile from backups.

Mature codebases end up with a *tiered* consistency model: a small kernel of strongly consistent state (identity, money, inventory) served by Raft-backed or single-primary stores, wrapped in a large surface of eventually consistent projections and caches, with explicit staleness budgets per view and idempotency at every write boundary. The team stops arguing about "strong vs eventual" and starts arguing about which data belongs in which tier — which is the right argument to be having.

## Further reading

- [Werner Vogels, "Eventually Consistent" — ACM Queue](https://queue.acm.org/detail.cfm?id=1466448) — the reference definition, still the shortest correct description of the model.
- [Alex DeBrie, "Understanding Eventual Consistency in DynamoDB"](https://www.alexdebrie.com/posts/dynamodb-eventual-consistency/) — the practical DynamoDB angle, including GSI gotchas.
- [Figma, "How Figma's multiplayer technology works"](https://www.figma.com/blog/how-figmas-multiplayer-technology-works/) — a real product engineering blog on choosing property-level LWW over full CRDT.
- [Liveblocks, "Understanding sync engines: Figma, Linear, and Google Docs"](https://liveblocks.io/blog/understanding-sync-engines-how-figma-linear-and-google-docs-work) — comparative look at three shipped sync engines and their conflict-resolution choices.
- [Terry et al., "Session Guarantees for Weakly Consistent Replicated Data" (Bayou, 1994)](https://www.cs.utexas.edu/~lorenzo/corsi/cs380d/papers/SessionGuaranteesPDIS.pdf) — the paper that names read-your-writes, monotonic reads, and the two others; still the mental model everyone uses.
- [Shapiro et al., "A comprehensive study of Convergent and Commutative Replicated Data Types"](https://hal.inria.fr/inria-00555588/document) — the CRDT paper. Skim it before designing your own merge function.
