# Sharding — Overview

> Sharding is splitting one logical database into many physical databases by a key, so each machine only owns a slice of the data.

## The 30-second version

Your database is doing fine until it isn't. One day the table has 500 million rows, the disk is full, and a single primary node can't keep up with writes. Sharding solves this by chopping the data horizontally — every row still looks the same, but rows live on different machines based on some routing rule (usually a hash of `user_id` or a range of dates). You scale by adding more boxes instead of buying a bigger one.

## The mental model

Imagine a single giant filing cabinet in your office. It works until the office has ten thousand employees. Now you can't fit it in the room and three people are always queued up at the drawers.

So you buy ten smaller filing cabinets and put one in each hallway. You make a rule: "if the customer's last name starts with A–C, their folder lives in cabinet 1; D–F in cabinet 2, and so on." Anyone looking up a folder first checks the rule, walks to the right cabinet, and pulls the file. Nobody ever has to open all ten cabinets to find one folder.

That's sharding. The rule is the **shard key**. The cabinets are **shards**. The receptionist who knows the rule is the **router** (or query layer). Every cabinet has the same drawer layout (same schema) — they just hold different folders.

The whole game is choosing a key where the folders end up spread evenly and most lookups only need to touch one cabinet.

## What it is NOT

- **Not replication.** Replication makes copies of the same data on multiple nodes for read scale or failover. Sharding splits *different* data onto different nodes.
- **Not vertical partitioning.** Vertical partitioning splits a table by *columns* (e.g., move `profile_blob` to a separate table). Sharding splits by *rows*.
- **Not the same as table partitioning.** Postgres or MySQL "partitions" usually live on the *same* server — same disk, same process. Sharding spans multiple servers.
- **Not free horizontal scaling.** It buys throughput at the cost of cross-shard joins, distributed transactions, and rebalancing pain.

## When you would reach for it

- A single primary node can't absorb the write rate, even after vertical scaling.
- The working dataset no longer fits in RAM on one machine and cache hit rates are collapsing.
- You need geographic locality — EU users on EU shards, US users on US shards — for latency or data residency.
- Tenants are naturally isolated (multi-tenant SaaS where each customer's data rarely mixes with others').

## When you would NOT reach for it

- Your database fits on one box and your problem is a missing index. Fix the index.
- You need ACID transactions that span many rows of unrelated entities — distributed transactions are slow and fragile.
- Your access patterns are heavily analytical with big joins across the whole dataset. Consider a data warehouse or columnar store instead.
- You're under 100 GB of hot data. Sharding's operational overhead almost certainly costs more than a bigger instance.

## Key vocabulary (just enough to keep reading)

- **Shard** — one physical database holding a slice of the data.
- **Shard key** — the column whose value decides which shard a row goes to (e.g., `user_id`).
- **Routing layer** — the component that maps a query to the right shard(s). Can be in the client, a proxy, or the DB itself.
- **Hash sharding** — `shard = hash(key) % N`. Even distribution, bad for range scans.
- **Range sharding** — `shard = lookup(key in [lo, hi])`. Great for range scans, prone to hotspots.
- **Directory / lookup sharding** — a separate table maps each key to a shard. Flexible, but the lookup itself becomes a bottleneck.
- **Hotspot** — one shard getting disproportionate traffic (e.g., your sharding by country and 60% of users are in one country).
- **Resharding** — redistributing data when you add or remove shards. The hardest part of operating sharded systems.
- **Cross-shard query** — a query that has to touch more than one shard. Slow, hard to transaction, easy to get wrong.

## What's next

The next document (`02-deep-dive.md`) answers What / Where / When / How / Why in detail — including shard key selection, consistent hashing, resharding strategies, and how systems like Vitess, Citus, and MongoDB actually implement this in production.
