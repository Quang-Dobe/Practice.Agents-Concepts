# Materialized View — Overview

> A materialized view is a query whose answer the database stores on disk as a real table, so reads are instant — at the cost of that answer being slightly out of date.

## The 30-second version

A normal SQL view is just a saved query. Every time you select from it, the database re-runs the whole thing. That is fine for a three-table join, and painful for a nightly aggregate over 400 million rows. A materialized view runs that query once, writes the result to physical storage, and serves later reads straight from those stored rows. You trade freshness for speed: the stored copy is correct as of the last refresh, not as of right now. Engineers reach for this the moment a dashboard query takes 40 seconds and the underlying numbers only change hourly anyway.

## The mental model

Think of a **recipe card versus leftovers in the fridge**.

A view is the recipe card. It tells the kitchen exactly how to make the dish, but nothing is cooked until someone orders. Every order means chopping, simmering, plating — full cost, every time, and the dish always reflects today's ingredients.

A materialized view is the dish already cooked, portioned, and sitting in a labeled container. Someone orders it, you hand it over in five seconds. But the container has a date on it. If a supplier changed the ingredients this morning, your portion still reflects yesterday's batch until you cook again.

"Cooking again" is the **refresh**, and that's where all the interesting engineering lives:

```
base tables  ──(query)──►  [ stored result rows ]  ──(fast read)──►  your app
                    ▲
              refresh: when? how much?
```

Two refresh styles matter. A **full refresh** throws the container out and cooks the whole batch again — simple, and expensive. An **incremental refresh** looks at only the rows that changed since last time and patches the stored result — cheap, but only possible for query shapes the engine knows how to patch.

Every database picks a different point on that dial. PostgreSQL gives you manual `REFRESH MATERIALIZED VIEW` (full rewrite; add `CONCURRENTLY` to avoid blocking readers, which requires a unique index on the view). Oracle offers on-demand or on-commit refresh, with fast incremental refresh backed by materialized view logs. SQL Server calls its version an *indexed view* and keeps it in sync synchronously on every write. Snowflake and BigQuery refresh theirs automatically in the background, with restrictions on what the query may contain.

That last sentence is a simplification of some real limits — the deep dive fixes it.

## What it is NOT

- **Not a regular view.** A view stores no data; it re-executes the query on every read.
- **Not a cache.** A cache is usually keyed lookups in a separate system with TTL eviction. A materialized view is a queryable relation inside the database, with real indexes and joins.
- **Not a replica.** A read replica copies the whole database; a materialized view copies one query's answer.
- **Not a table you maintain yourself.** The engine owns the refresh contract — that's the entire point over a hand-rolled summary table.
- **Not a stream processor.** Flink or RisingWave compute continuously over events; classic materialized views recompute on a schedule or trigger.

## When you would reach for it

- A dashboard aggregate (daily revenue by region) that is read thousands of times and changes once an hour.
- An expensive multi-table join that many different queries all start from.
- Precomputing a search or ranking projection so the hot path never touches the raw fact table.
- Denormalizing across services' tables inside a reporting database.

## When you would NOT reach for it

- The consumer needs exact current values — billing balances, inventory counts at checkout.
- The base tables churn faster than the refresh can complete, so you pay refresh cost constantly and still serve stale data.
- The query is already fast; a plain index would have solved it.
- You need per-user filtered results — you'd be materializing a combinatorial explosion.

## Key vocabulary (just enough to keep reading)

- **Base table** — the source table(s) the view's query reads from.
- **Refresh** — recomputing the stored result so it matches the base tables again.
- **Full refresh** — recompute everything and replace the stored rows.
- **Incremental / fast refresh** — apply only the changes since the last refresh.
- **Staleness** — how far behind the base tables the stored result currently is.
- **Refresh lag / target lag** — the freshness bound you promise consumers (Snowflake names it `TARGET_LAG`).
- **Query rewrite** — the optimizer silently redirecting a query against base tables to the materialized view instead.
- **Materialized view log** — Oracle's change journal that makes incremental refresh possible.
- **Indexed view** — SQL Server's name for a materialized view kept in sync on every write.

## What's next

The next document answers What / Where / When / How / Why in detail: how refresh algorithms actually work, what each engine supports, how query rewrite decides to use your view, and how to reason about the staleness budget.
