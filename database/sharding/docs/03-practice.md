# Sharding — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical multi-tenant SaaS backend, sharding is the load-bearing piece that sits between your API tier and your relational database once a single primary stops absorbing the write rate. The shape is almost always the same: a routing component (Vitess `vtgate`, MongoDB `mongos`, a Citus coordinator, or a homemade proxy like Figma's `DBProxy`) in front of N MySQL/Postgres clusters, each cluster itself replicated for HA.

In a consumer product with a social graph or feed (chat, marketplaces, dashboards), sharding is the reason `user_id = ?` queries stay under 10 ms while the table has 50 billion rows. Pick the wrong key here and the next viral post takes one shard down.

In a serverless-database backend (DynamoDB, Cosmos DB, Aurora DSQL, Spanner), you "don't operate sharding" but you absolutely still meet it — through partition keys, RCU/WCU per-partition limits, and a surprise bill when one tenant generates 80% of writes. Understanding the abstraction is the difference between a $200 and a $20,000 monthly bill.

In a regulated workload (EU vs US data residency, healthcare per-region), sharding is the mechanism that pins customer X's data to a specific physical region for compliance, not a performance reason.

## Best practices

### 1. Choose the shard key based on the dominant query, not on the data model
**Do:** Pick the key that appears in the `WHERE` clause of >95% of your hot-path queries. In a multi-tenant SaaS that is almost always `tenant_id` or `customer_id`. Verify with real query logs, not intuition.
**Why:** Any read that lacks the shard key triggers a scatter-gather across all shards. Tail latency rises linearly with shard count, and one slow shard sets p99 for everyone.
**Avoid:** Sharding by `created_at` or `id` because it "feels even" — that breaks every per-tenant query.

### 2. Hash, don't range, unless you have a concrete reason
**Do:** Default to hash sharding (or consistent hashing with vnodes). Reach for range only when range scans dominate (time-series, append-only logs with TTL-based deletion) and you have a plan for the tail-end hotspot.
**Why:** Range sharding plus a monotonic key (auto-increment ID, `now()`) sends every new write to the rightmost shard. You scaled horizontally and bought yourself a single-writer system.
**Avoid:** Range sharding on `timestamp` for a high-write event stream without bucketing or salting.

### 3. Pick a shard key that is immutable, high-cardinality, and well-distributed
**Do:** The key must (a) never change for a given row, (b) have far more distinct values than you'll ever have shards, and (c) have access skew under roughly 4x between the hottest and median value.
**Why:** Mutable shard keys mean "moving" a row across shards on every update — most engines forbid it outright. Low-cardinality keys (`country_code`, `plan_tier`) create jumbo chunks in MongoDB or unbalanceable ranges in Vitess that the balancer cannot split.
**Avoid:** `status`, `region`, `is_active`, or anything Boolean-ish.

### 4. Co-locate related entities under the same shard key
**Do:** Shard `users`, `orders`, `invoices`, and `audit_log` all by the same `customer_id`. Citus calls this *distribution column* alignment; Vitess uses *keyspace IDs*; DynamoDB uses partition+sort keys with the same partition key for the item collection.
**Why:** Local joins and single-shard transactions stay cheap. The moment you split a parent and its children across shards, every join becomes scatter-gather and every write becomes a 2PC candidate.
**Avoid:** Sharding `orders` by `order_id` while `users` is sharded by `user_id`. Every "show me this user's orders" now touches every shard.

### 5. Treat cross-shard transactions as a code smell, not a feature
**Do:** Design the data model so multi-row writes stay on one shard. When truly unavoidable, use idempotent, retry-safe operations with a saga or outbox pattern; reserve 2PC (Vitess `TwoPC`, Spanner) for the few flows that genuinely need atomicity.
**Why:** Cross-shard 2PC commit latency is bounded below by the slowest participant — typically 2–10x a single-shard write. A coordinator crash mid-prepare stalls every participant until manual cleanup.
**Avoid:** Turning on 2PC globally because "transactions should just work."

### 6. Build the routing layer before you need a second shard
**Do:** Even on a single physical DB, introduce a routing abstraction (proxy or library) and write all queries *as if* they were sharded — i.e., always carry the shard key in the predicate. Figma did this with logical "sharded views" on one Postgres instance before any physical split.
**Why:** Adding the shard key to 1,400 call sites under time pressure during an incident is the worst possible time to do it. Doing it cold, in feature work, takes a fraction of the engineering.
**Avoid:** "We'll add the routing layer when we shard." You won't; you'll be too busy firefighting.

### 7. Monitor *per-shard* metrics, not just cluster aggregates
**Do:** Dashboards must show CPU, IOPS, replication lag, p99 latency, QPS, and storage **broken down by shard**. Alert on per-shard skew (e.g., any shard >2x the median) before alerting on cluster totals.
**Why:** A hot shard at 95% CPU averages out to a healthy-looking 40% across an 8-shard cluster. Your dashboards will look fine until the hot shard tips over.
**Avoid:** Reusing a single-node monitoring dashboard and adding `sum()` everywhere.

### 8. Practice resharding before you need it
**Do:** Run a resharding drill in staging on realistic data volumes at least once per quarter. Measure how long copy + catch-up + cutover actually takes for your largest shard. Kill the source primary mid-copy and confirm recovery.
**Why:** Resharding under pressure (a hot shard is degrading prod *now*) is when teams discover their bulk-copy can't keep up with live write rate — the "resharding lag spiral" in the deep dive.
**Avoid:** Assuming the vendor docs' "few seconds of read-only cutover" applies at your write rate without measuring.

### 9. Prefer vendor-managed sharding unless you have an SRE team
**Do:** For most teams under ~5 engineers on data infrastructure, Vitess (PlanetScale), Citus (Azure Cosmos DB for Postgres), MongoDB Atlas sharded clusters, or DynamoDB/Spanner is the right answer. DIY application-level sharding is a five-year commitment.
**Why:** Online DDL, resharding, topology management, and shard-aware connection pooling are each multi-quarter projects. Vendors have solved them; your team will get to "barely works" and stall.
**Avoid:** Rebuilding ProxySQL with custom routing rules because "it's just a hash function."

### 10. Plan for schema migrations across N shards from day one
**Do:** Use a tool that applies DDL to every shard atomically with rollback (Vitess `OnlineDDL`, gh-ost coordinated across shards, Citus's coordinator-driven DDL). Verify schema parity in CI.
**Why:** A migration that succeeds on 7/8 shards leaves one shard with a missing column. Every query touching that column fails for 1/8 of your users — a partial outage that's hard to diagnose.
**Avoid:** A bash loop that runs `ALTER TABLE` against each shard and hopes.

## Anti-patterns to recognize

- **Sharding too early.** Splitting a 40 GB database into 8 shards "to be ready." You inherit the operational tax (rebalancing, cross-shard joins, distributed schema migrations) without the throughput need; a bigger box would have cost less for two years. Fix indexes and add read replicas first.
- **Sharding by auto-increment ID with range partitioning.** Every new row lands on the last shard, so writes don't scale and old shards go cold. Use hash sharding, or shard by a tenant key.
- **The "one tenant to rule them all" shard key.** Sharding by `customer_id` when one customer has 60% of the data. The whale tenant saturates one shard. Either split that tenant across sub-keys (`customer_id, sub_partition`) or move the whale to a dedicated shard via a directory map.
- **Mutable shard key.** Sharding users by their *current* `region`, then letting users change region. The row now belongs on a different shard but is physically on the old one; queries silently return empty results. Use an immutable surrogate or a directory layer.
- **Trusting adaptive capacity to save a bad partition key (DynamoDB).** "Adaptive capacity will handle it" — until a burst exceeds the per-partition 1,000 WCU ceiling for 5+ minutes before split-for-heat kicks in. Design for even distribution; adaptive capacity is a safety net, not a strategy.
- **Cross-shard query creep.** A small product change adds a "show me all pending orders" admin screen. It looks fine in dev with 3 shards; in prod it scatters to 64 shards every page load and tail latency triples. Gate non-shard-key queries behind explicit code review.
- **Treating reference data like sharded data.** Shipping `countries`, `currencies`, or `feature_flags` through the same sharding path. Replicate them to every shard as *reference tables* (Citus) or *lookup vindexes* (Vitess) so joins stay local.
- **Routing layer with no cache, or with an unbounded cache TTL.** No cache means the topology store (etcd, config servers) becomes the hot path. Forever-cached topology means clients route to a shard that has been retired. Cache the shard map with a 1–10 second TTL and a push-based invalidation.

## Real-world usage patterns

**Multi-tenant B2B SaaS at 20k tenants.** Hash-sharded by `tenant_id` across 16 MySQL shards behind Vitess. ~98% of queries carry `tenant_id`. The remaining 2% (admin reports, billing rollups) run against a separate analytics replica fed by CDC. Non-obvious lesson: the *analytics* path absorbs all the scatter-gather queries, which is what makes the OLTP path stay clean. Without that escape valve, product engineers eventually sneak a cross-shard query into the hot path.

**Consumer social feed at 50M MAU.** Sharded by `user_id` with consistent hashing; per-user data (profile, follows, settings) stays single-shard. The feed itself uses a separate fan-out service backed by Redis. Non-obvious lesson: the team explicitly chose *not* to shard posts by `post_id` because joining "user + their posts" became the dominant query — they keep posts on the author's shard.

**Time-series telemetry at 200k writes/sec.** Range-sharded by `(device_id_hash, hour_bucket)`. Hash on the device prevents the rightmost-shard hotspot; hour bucket allows efficient deletion of old data by dropping whole shards. Non-obvious lesson: TTL-by-dropping-shards is the cheapest delete you'll ever do — orders of magnitude faster than `DELETE WHERE timestamp <`.

**DynamoDB-backed event store.** Partition key is `tenant_id#YYYYMMDD` (salted with the date to spread one tenant's writes across partitions). Non-obvious lesson: the team added the date suffix only after a single large tenant hit the per-partition WCU cap; they wish they had built it on day one because backfilling the new key required dual-writes for 6 weeks.

**Figma-style pre-sharding on a single Postgres.** Logical shards as Postgres views, routed by `DBProxy`, with physical sharding deferred. Non-obvious lesson: the *interface* (every query carries a shard key, every transaction stays single-shard) is what unlocked the eventual physical migration in days instead of months.

## Operational checklist

- **Monitoring:** Are per-shard CPU, QPS, p99 latency, replication lag, and storage on the dashboard? Is there an alert when any shard exceeds 2x the median?
- **Hot-key detection:** Is there a job that samples the top-100 shard-key values by traffic over a 5-minute window? Would you notice a celebrity hot key within an hour?
- **Resharding:** Has a full resharding drill been run in staging in the last 90 days, including a forced primary kill mid-copy?
- **Schema migrations:** Does the DDL tool apply changes to all shards atomically with rollback, and does CI check for schema drift between shards?
- **Cross-shard queries:** Is there a lint / proxy rule that flags queries lacking the shard key in the predicate? Are scatter-gather queries on a budget?
- **Failure handling:** What happens if one shard's primary is unreachable for 60 seconds? Do writes fail fast, queue, or hang? Is that behavior documented and tested?
- **Security:** Are credentials per-shard, rotated independently? Can a compromised app instance read from shards beyond its tenant scope?
- **Cost:** Does the bill break out per-shard cost? Is there a per-tenant cost view so a whale tenant is visible before billing surprises?
- **Backups:** Are backups consistent *across* shards (point-in-time snapshot of the whole logical DB), or only per-shard? Cross-shard restores need cross-shard snapshots.
- **Onboarding:** Can a new engineer name the shard key, the routing layer, and the resharding tool on day one? Is there a runbook for "a single shard is on fire"?

## How this topic typically evolves in a codebase

Teams almost always start with no sharding, one Postgres or MySQL primary, and a few read replicas. The first scaling moves are vertical (bigger instance), then more replicas, then caching (Redis in front of hot reads), then a partial split — usually pulling one heavy table (events, audit log) onto its own database. None of that is sharding yet. The pain point that finally forces the move is almost always *write* throughput on the primary, or a working set that no longer fits in RAM.

The painful migration point is the first physical shard split. By then the codebase has hundreds of queries written under the assumption of a single database. Half don't carry a shard key in their predicate; some join across what will become shard boundaries. The Figma-style move — introduce a routing proxy and logical sharding *before* any physical split — is the modern best practice for getting through this without an outage. Teams that skip it tend to spend 6–18 months in a half-migrated state where some tables are sharded and some aren't, with elaborate conditional routing.

Mature sharded codebases tend to converge on: a vendor-managed control plane (Vitess, Citus, DynamoDB), strict lint rules around shard-key predicates, a separate analytics pipeline for non-shard-key queries, and per-tenant cost dashboards. The next evolution after that is usually *multi-region* sharding for residency and latency — which restarts a lot of these decisions because the routing function now also encodes geography.

## Further reading

- [How Figma's Databases Team Lived to Tell the Scale](https://www.figma.com/blog/how-figmas-databases-team-lived-to-tell-the-scale/) — the reference write-up for staged horizontal sharding on Postgres; the logical-before-physical pattern is the most copied idea in this space right now.
- [Lessons learned from 10 years of DynamoDB](https://www.amazon.science/blog/lessons-learned-from-10-years-of-dynamodb) — Amazon's own retrospective on hot partitions, adaptive capacity, throughput dilution, and why partition-key design is a system problem, not a capacity problem.
- [Vitess Resharding documentation](https://vitess.io/docs/20.0/reference/vreplication/reshard/) — the canonical operational playbook for online resharding of MySQL: copy, catch-up, verify, cutover. Read before any production split.
- [Scaling DynamoDB: partitions, hot keys, and split-for-heat (AWS Database Blog)](https://aws.amazon.com/blogs/database/part-2-scaling-dynamodb-how-partitions-hot-keys-and-split-for-heat-impact-performance/) — concrete numbers on per-partition limits and how the platform reacts to skew; useful even if you're not on DynamoDB.
- [MongoDB shard key troubleshooting](https://www.mongodb.com/docs/manual/core/sharding-troubleshooting-shard-keys/) — jumbo chunks, low-cardinality keys, and the limits of `refineCollectionShardKey`. Translates directly to other systems.
- [Designing Data-Intensive Applications, Ch. 6 "Partitioning"](https://dataintensive.net/) — Kleppmann's chapter is the cleanest theoretical grounding for everything above. Re-read it once a year.
