# Sharding — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Sharding is **horizontal partitioning of a logical dataset across independent physical database instances**, where each instance (a *shard*) is authoritative for a disjoint subset of the keyspace and accepts both reads and writes for its slice. A *routing function* `f(shard_key) → shard_id` decides ownership. Rows are not replicated across shards (replication is an orthogonal concern that happens *within* a shard). The defining property is that the union of all shards reconstitutes the original logical table, and the intersection of any two shards is empty.

This is a *share-nothing* architecture in Stonebraker's sense: shards do not share memory, disk, or a write log. Coordination across shards is explicit and costly.

### The core building blocks

- **Shard key** — the column(s) the routing function consumes. Choice is permanent in most systems; changing it requires a full data migration.
- **Routing function** — pure function from key to shard. Three families: hash (`hash(key) % N`, or consistent hashing), range (`key ∈ [lo, hi)`), or directory (lookup in a metadata table).
- **Router / query layer** — the component that executes the routing function and forwards the query. Lives in the client driver (Cassandra), a proxy (Vitess `vtgate`, MongoDB `mongos`), or the database itself (Citus coordinator).
- **Shard map / topology** — metadata describing which shard owns which range. Stored in a strongly-consistent store: Zookeeper (Vitess pre-v14), etcd (Vitess current), Spanner's universe master, MongoDB's config servers (a replica set).
- **Rebalancer / mover** — background process that migrates data between shards when adding capacity or correcting skew. Examples: MongoDB's *balancer*, Vitess's *VReplication*, Citus's `rebalance_table_shards()`.
- **Reference / global tables** — small, mostly-read tables (currencies, country codes) copied to every shard so joins don't go cross-shard. Citus calls these *reference tables*; Vitess calls them *lookup vindexes* or *reference tables*.

### How it relates to the broader landscape

Sharding is one member of the data-distribution family, alongside **replication** (same data, many nodes, for fault tolerance and read scale), **federation** (different *services* own different *tables*), and **vertical partitioning** (different columns to different stores). Sharded systems almost always also replicate each shard for HA — Cassandra and DynamoDB collapse the two concerns into one ring; MongoDB and Vitess keep them separate (each shard is itself a replica set / MySQL primary+replicas). NewSQL systems like Spanner, CockroachDB, and TiDB do sharding automatically at the storage layer, hiding it behind a SQL surface.

## Where

### Where it runs / lives in the stack

At the **data tier**, but the *routing* component can sit at three layers:

1. **In the client** (driver-side): Cassandra's driver computes the token from the partition key and contacts the owning replica directly. Lowest latency, but every client must know the topology.
2. **In a proxy** (middleware): `vtgate` (Vitess), `mongos` (MongoDB), ProxySQL, Figma's `DBProxy`. The client speaks unmodified SQL; the proxy parses, rewrites, and routes. Adds a hop but centralizes the routing logic.
3. **In the database engine** (transparent): Spanner, CockroachDB, YugabyteDB, Citus. The engine handles sharding internally; the client sees a single SQL endpoint.

### Where you typically encounter it

- **Vitess** — YouTube, Slack, GitHub, Shopify run sharded MySQL through it.
- **Citus** — distributed Postgres extension, now Azure Cosmos DB for PostgreSQL.
- **MongoDB sharded clusters** — native sharding via `mongos` and config servers.
- **Cassandra / ScyllaDB** — consistent-hashing ring, no router process.
- **DynamoDB** — partitioning is automatic and invisible; you only see the partition key.
- **Spanner / CockroachDB / TiDB** — automatic range-sharded storage under a SQL surface.
- **Elasticsearch / OpenSearch** — index sharding for search workloads.

### Ecosystem and tooling

- **For sharded SQL**: Vitess (MySQL), Citus (Postgres), PlanetScale (managed Vitess), TiDB, CockroachDB.
- **For sharded document/KV stores**: MongoDB, DynamoDB, Cassandra, ScyllaDB, Couchbase.
- **For routing/proxy layers you build yourself**: ProxySQL, Envoy with custom filters, application-level sharding libraries (e.g., ShardingSphere for JDBC).
- **For schema migration across shards**: gh-ost, pt-online-schema-change, Vitess's `OnlineDDL`.
- **For shard-aware ORMs**: Django's database routers, Rails' `connects_to`, ShardingSphere-JDBC.

## When

### When the topic emerged and why

The original commercial sharding effort is widely attributed to Google's late-90s search index and to eBay's early-2000s "functional and horizontal segmentation". The term entered mainstream vocabulary via the 2006 Flickr engineering post "Federation at Flickr: Doing Billions of Queries Per Day", and Friendster/MySpace-era LiveJournal popularized the user-bucket pattern. The motivation was identical everywhere: a single MySQL primary could not absorb the write rate, and per-machine RAM/disk could not hold the working set. NoSQL systems (Dynamo 2007, BigTable 2006, Cassandra 2008) then made sharding a built-in design property rather than an application-level retrofit.

### When to use it in a project

Reach for it when:

- A single primary can't take the write rate and you've already exhausted vertical scaling, read replicas, and connection pooling.
- The hot working set exceeds RAM on the largest economical instance (typically 256 GB–2 TB depending on cloud SKU).
- Data residency requires geographic partitioning (EU users on EU shards under GDPR Article 44+).
- You have a natural tenant boundary (per-customer, per-region) where >95% of queries scope to a single tenant.
- You're already on a system with first-class sharding (Vitess, Spanner, DynamoDB) and adding a shard is operationally cheap.

### When NOT to use it

Avoid it when:

- Your bottleneck is a missing index, an N+1 query, or unbatched writes. Profile first.
- Workload is OLAP with full-table aggregations — use a columnar warehouse (Snowflake, BigQuery, ClickHouse).
- You need cross-entity ACID transactions touching arbitrary rows. Cross-shard 2PC is real but slow.
- Total hot data is under ~100 GB. The operational tax (shard map, rebalancing, schema migrations across N shards) exceeds the savings.
- The team has never operated a distributed database before and has no SRE bandwidth.

## How

### How it works under the hood

A read/write through a sharded system follows roughly this lifecycle. Concrete example: a Vitess cluster with 4 shards keyed on `customer_id`.

1. **Parse**: the router (`vtgate`) parses the SQL and identifies predicates on the shard key — e.g., `WHERE customer_id = 12345`.
2. **Route**: it applies the *vindex* (Vitess's name for the routing function), commonly `hash(12345) → 0x4A...` mapped to keyspace range `[0x40, 0x80)`, which currently owns shard `-80`.
3. **Look up topology**: the shard-to-tablet mapping is read from the topology service (etcd or Consul), usually cached in-process with a TTL of a few seconds.
4. **Forward**: the query is sent to the primary tablet of shard `-80`, which is a normal MySQL instance.
5. **Execute locally**: MySQL plans and runs the query against its slice (which is just an ordinary InnoDB table containing only the rows for that key range).
6. **Return**: result rows stream back through `vtgate` to the client.

For queries *without* the shard key in the predicate (`SELECT * FROM orders WHERE status='pending'`), the router does **scatter-gather**: send the query to all shards in parallel, then merge results (deduplicate, sort, aggregate) at the router. Cost scales with the number of shards.

**Resharding** (splitting shard `-80` into `-60` and `60-80` because it grew hot) typically follows:

1. Spin up the two new shards empty.
2. Snapshot the source shard at a logical position (binlog GTID for MySQL, change-feed cursor for MongoDB).
3. Bulk-copy rows whose hash falls in each new range to the corresponding destination.
4. Tail the binlog and apply ongoing writes to both destinations (Vitess `VReplication`, MongoDB chunk migration with critical-section catch-up).
5. Verify row counts and checksums; throttle if replication lag grows.
6. Atomic cutover: briefly stop writes (Vitess reports a few seconds of read-only), update the shard map in topology, resume on new shards.
7. Drop or archive the old shard.

The Figma team's 2023 work avoided physical resharding for as long as possible by creating *sharded views* on a still-unsharded Postgres instance, routing through their `DBProxy`, then flipping reads/writes via feature flag and reverting in seconds if needed.

### Key trade-offs

| Design choice | What you gain | What you give up |
|---|---|---|
| Hash sharding | Even distribution, no hotspots from monotonic keys | Range scans become scatter-gather |
| Range sharding | Locality for time-series and range queries | Tail-end hotspots (latest timestamp = one shard) |
| Directory sharding | Arbitrary key-to-shard mapping; easy to rebalance one tenant | The lookup table is a SPOF and a cache target |
| Consistent hashing with vnodes (Cassandra) | Adding a node moves only `1/N` of keys | Per-node memory for vnode tokens; less precise control |
| Jump consistent hash (Google 2014) | Zero memory, near-perfect distribution, O(ln N) | Cannot remove an arbitrary bucket — only the last one |
| Co-locating related tables on the same shard key (Citus, Vitess) | Local joins, single-shard transactions | All tables sharing the key must reshard together |
| Cross-shard 2PC (Vitess `TwoPC`, Spanner) | Atomic multi-shard writes | Commit latency dominated by slowest participant; coordinator failure stalls participants |

### Common failure modes

- **Celebrity hot key** — one `user_id` (Justin Bieber on Twitter, a viral post on Reddit) gets 100x the traffic of any other; the shard owning it saturates while peers idle.
- **Monotonic shard key on range sharding** — sharding `events` by timestamp puts every new write on the rightmost shard. Classic with auto-increment IDs too.
- **Low cardinality shard key** — sharding by `country_code` when 70% of users are in the US. MongoDB will create at most one chunk per unique value, so you end up with a giant US chunk.
- **Cross-shard query storm** — a feature change adds a query without the shard key in its predicate; suddenly every request scatters to all shards and tail latency explodes.
- **Resharding lag spiral** — bulk copy can't keep up with the write rate of the source shard, so VReplication / balancer never catches up.
- **Shard-map cache staleness** — clients route to the old owner after a split; reads return phantom-empty results, writes go to a tablet that has already been demoted.
- **DynamoDB hot partition** — a single partition key exceeds the hard 3,000 RCU / 1,000 WCU per-partition ceiling; adaptive capacity and "split for heat" help but with a delay.
- **Schema migration drift** — a DDL succeeds on 7 of 8 shards and fails on the 8th; queries hit a column that doesn't exist on one shard.

## Why

### Why it exists

Sharding addresses the fundamental fact that a single machine has finite CPU, RAM, disk, and network. Once a workload exceeds that ceiling, you have two options: build a more expensive single machine (vertical), or split the work across many machines (horizontal). Vertical scaling hits physics (the largest cloud VMs cap around a few TB of RAM and tens of thousands of IOPS) and a price curve where the top SKU costs more than 10x the median. Horizontal scaling — which sharding is the specific form of for stateful storage — is the only path that keeps the cost-per-unit roughly linear.

Sharding also addresses two other first-principles concerns: **blast radius** (a failed shard takes down 1/N of users, not 100%) and **geographic locality** (a shard in Frankfurt serves EU users with sub-20 ms latency that a Virginia primary cannot).

### Why it looks the way it does

The "shard key chosen up-front, routing function is essentially fixed" design is non-obvious. An alternative — used by some early systems and recently by Spanner-class engines — is **automatic range splitting** with no user-chosen key: the system observes load and splits ranges as needed. Why didn't that win for MySQL/Postgres-based sharding?

Two reasons. First, automatic splitting requires the storage engine to expose stable, ordered ranges as a first-class primitive (Spanner's *splits*, CockroachDB's *ranges*); InnoDB and Postgres heap files don't, so retrofitting requires building an entirely new storage layer. Second, application-level co-location of related rows (a user and their orders on the same shard for a fast join) is much easier to reason about when the key is explicit and stable. The trade was: give the developer one annoying decision (the key) in exchange for predictable single-shard transactions and joins.

Similarly, **consistent hashing with virtual nodes** (Cassandra, Dynamo) beat plain modulo hashing because `hash(k) % N` moves `(N-1)/N` of the keys when `N` changes. Consistent hashing moves only `K/N`. Jump consistent hash is more efficient still but constrains you to only removing the last bucket, which is fine for stateless caches but unacceptable for storage where a specific node may need to be retired.

### Why it matters now

In 2026, the relevance is shifting rather than fading. Pure DIY sharding (Flickr-style application-level user buckets) is in retreat — too operationally expensive for most teams. But the *concept* is more pervasive than ever, just hidden: every serverless database (DynamoDB, Cosmos DB, Aurora DSQL, Spanner, Turso) is sharded under the surface, and understanding shard keys is still the difference between a $200/month bill and a $20,000/month bill. Vitess, Citus, and Spanner all shipped significant resharding and online-DDL improvements in 2024–2025, and Figma's 2023 sharding write-up has become a reference architecture for teams hitting the same wall. If you work on any system above mid-five-figure QPS or low-TB data, sharding will be in your stack — the only question is whether you operate it directly or pay a vendor to hide it.

## Open questions / things to verify in practice

- For your specific workload, what fraction of queries actually include the shard key in their `WHERE` clause? Anything below ~95% will hurt under scatter-gather.
- What is the actual cardinality and access skew of your candidate shard key? Run `SELECT key, COUNT(*) FROM t GROUP BY key ORDER BY 2 DESC LIMIT 100` before committing.
- How does the system you're using handle a node failure during resharding? Test a forced kill of the source primary mid-copy, not just a clean shutdown.
- What's the real latency cost of a 2PC commit in your environment? Measure p50/p99 with and without cross-shard transactions enabled.
- How does the schema-migration tool handle partial failures across N shards? Force one shard to fail mid-migration and observe recovery.
- For DynamoDB/Cosmos: what's your write distribution per partition key over a 5-minute window? Is anyone approaching the 1,000 WCU per-partition cap?
