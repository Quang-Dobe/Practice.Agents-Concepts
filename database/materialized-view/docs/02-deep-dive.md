# Materialized View — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

A materialized view is a **named database relation whose extent is defined by a query over base relations, whose rows are physically stored, and whose consistency with those base relations is maintained by the engine under a stated refresh contract**.

Three clauses matter, and each is load-bearing:

- **Defined by a query** — the definition is declarative SQL, not an ETL script. The engine knows the derivation, so it can reason about it (rewrite queries, decide whether incremental maintenance is legal).
- **Physically stored** — it occupies pages, can carry its own indexes, partitioning, clustering keys, and statistics.
- **Maintained under a contract** — synchronous (transactionally consistent with base tables), deferred (refreshed on demand or on schedule), or bounded-lag (fresh within *N* minutes). This contract is the only real difference between a materialized view and a summary table you maintain by hand.

In the literature this is the **view maintenance problem**: given view `V = Q(R₁…Rₙ)` and a set of changes `ΔRᵢ`, compute `ΔV` without re-evaluating `Q` from scratch.

### The core building blocks

- **View definition (the query)** — determines everything else. Whether the engine can maintain it incrementally is a property of the query's *shape*, not its size.
- **Backing storage** — a real heap/columnar segment. In PostgreSQL it is a relation with `relkind = 'm'` in `pg_class`; in SQL Server the storage *is* the unique clustered index on the view.
- **Change capture** — the delta source. Oracle uses a **materialized view log** (`CREATE MATERIALIZED VIEW LOG ON t WITH ROWID, SEQUENCE, PRIMARY KEY INCLUDING NEW VALUES`); Redshift and BigQuery use internal change tracking; ClickHouse uses the incoming insert block itself.
- **Refresh engine** — the code that turns deltas (or a full recompute) into stored rows, and decides the locking/visibility behaviour while doing so.
- **Query rewrite / view matching** — the optimizer rule that redirects a query written against base tables to the view. Present in Oracle, SQL Server (Enterprise/Azure SQL), Snowflake, BigQuery ("smart tuning"), Redshift. **Absent in PostgreSQL** — there you must name the view explicitly.
- **Staleness metadata** — `last_refresh`, `staleness`/`compile_state` (Oracle `USER_MVIEWS`), Snowflake `SHOW MATERIALIZED VIEWS`, Redshift `SVL_MV_REFRESH_STATUS`.
- **Delta algebra** — the formal model. `count`-based maintenance and **DRed** (Delete-and-Rederive) come from Gupta, Mumick & Subrahmanian, *Maintaining Views Incrementally*, SIGMOD 1993. The modern generalization is [DBSP (PVLDB 16:1601, 2023)](https://www.vldb.org/pvldb/vol16/p1601-budiu.pdf), which underpins Feldera.

### How it relates to the broader landscape

Materialized views sit in the family of **derived-data structures**, alongside secondary indexes (a materialized projection + sort), covering indexes, OLAP cubes, denormalized read models in CQRS, and streaming state in Flink/Materialize/RisingWave. The axis that separates them is *who owns consistency and at what latency*. An index is a materialized view the engine maintains synchronously and hides from you. A CQRS read model is a materialized view your application maintains asynchronously with no engine guarantees. A materialized view is the middle: declarative like an index, deferrable like a read model.

## Where

### Where it runs / lives in the stack

Inside the database engine, spanning three subsystems: the **catalog** (definition, dependency graph, staleness state), the **storage layer** (the stored rows and their indexes), and the **optimizer** (rewrite/matching rules). Maintenance work runs either in the writer's transaction (SQL Server indexed views, Oracle `ON COMMIT`), in a background service on shared compute (Snowflake, BigQuery, Redshift auto-refresh), or in a session you drive yourself (PostgreSQL `REFRESH`).

### Where you typically encounter it

- **PostgreSQL** — `CREATE MATERIALIZED VIEW` (9.3, 2013), `REFRESH … CONCURRENTLY` (9.4, 2014). Full recompute only; still true through PG 18.
- **Oracle** — the most complete implementation: `REFRESH FAST | COMPLETE | FORCE`, timed `ON DEMAND | ON COMMIT | ON STATEMENT`, plus real-time MVs (`ENABLE ON QUERY COMPUTATION`, 12.2+) that serve a stale MV plus a live delta.
- **SQL Server / Azure SQL** — indexed views: synchronous, no staleness, heavy write cost.
- **Snowflake** — Enterprise Edition only, single table, no joins, background maintenance. Multi-table work goes to **dynamic tables** (`TARGET_LAG`, minimum 1 minute).
- **BigQuery** — automatic incremental refresh over base-table deltas; `max_staleness` must be between 30 minutes and 3 days.
- **Redshift** — incremental refresh, `AUTO REFRESH`, plus **AutoMV**: the engine creates its own materialized views from workload patterns (limit 200 per database; creation stops at 80% cluster capacity, existing ones may be dropped at 90%).
- **ClickHouse** — its `MATERIALIZED VIEW` is not a stored query result but an **insert trigger** on the left-most source table, evaluated on the arriving block in memory. Refreshable MVs (23.12+) are the closer analogue.

### Ecosystem and tooling

- **For incremental maintenance in PostgreSQL:** [`pg_ivm`](https://github.com/sraoss/pg_ivm) (1.12, Sept 2025; supports PG 13–18) adds `create_immv()` with AFTER-trigger-driven maintenance. Query support is limited and writes are serialized during maintenance.
- **For continuous / streaming views:** Materialize (differential dataflow), RisingWave, Feldera (DBSP), Flink SQL, ClickHouse incremental MVs.
- **For orchestrating refresh:** dbt `materialized='materialized_view'`, Snowflake dynamic tables, Databricks materialized views (Lakeflow Declarative Pipelines), `pg_cron`.
- **For diagnosing rewrite:** Oracle `DBMS_MVIEW.EXPLAIN_MVIEW` and `EXPLAIN_REWRITE`, SQL Server `OPTION (EXPAND VIEWS)` to disable matching, BigQuery `EXPLAIN`/job stats.
- **For hierarchical rollups:** TimescaleDB continuous aggregates (PostgreSQL extension, incremental by time bucket).

## When

### When the topic emerged and why

The concept was formalised in the data-warehouse era. Oracle shipped **snapshots** in 7.x (1992) for replication, then renamed and generalised them to materialized views in **8i (1999)**, adding query rewrite so a star-schema query against a 1-billion-row fact table could be silently answered from a 10,000-row aggregate. SQL Server added indexed views in **2000**. The motivation was the same everywhere: OLAP queries scanned orders of magnitude more rows than they returned, and re-scanning per query was pure waste. Before materialized views, teams hand-built summary tables and hand-wrote the "which table do I query?" logic in the application — the exact thing query rewrite deletes.

### When to use it in a project

Reach for it when:

- Read:write ratio on the derived result is high (hundreds of reads per underlying change) and the aggregation factor is large (millions of base rows collapse to thousands).
- Consumers tolerate a stated staleness bound, and you can *write that bound down* (e.g. "≤ 15 minutes").
- The same expensive sub-plan (a wide join, a `GROUP BY` over a fact table) appears in many queries.
- You want the optimizer to route queries for you — Oracle, SQL Server Enterprise, Snowflake, BigQuery, Redshift.
- Refresh cost is predictable and fits in a window: full-refresh time must stay well below the refresh interval.

### When NOT to use it

Avoid it when:

- Consumers need read-your-own-writes (balances, inventory at checkout). Only synchronous maintenance gives this, and it taxes every write.
- Base tables churn at a rate comparable to refresh duration — you burn compute continuously and still serve stale data.
- The result is per-user or per-tenant filtered — you would materialize a combinatorial explosion. Materialize the shared aggregate; filter at read time.
- A composite or covering index solves it. Indexes are maintained incrementally and cheaply by definition.
- The write path is latency-sensitive and you are considering SQL Server indexed views or Oracle `ON COMMIT`. Microsoft's own guidance: DML against a table referenced by many or complex indexed views can degrade significantly, sometimes to the point where no plan is produced.

## How

### How it works under the hood

Take PostgreSQL's `REFRESH MATERIALIZED VIEW CONCURRENTLY` as the concrete walk-through (`src/backend/commands/matview.c`):

1. The matview is opened under `ExclusiveLock` — `SELECT` still works, a second refresh does not. Plain `REFRESH` takes `AccessExclusiveLock` and blocks readers outright.
2. The view query is executed into a **new temporary heap**, then `ANALYZE`d so the planner has statistics for the next step.
3. A **diff table** is built via `FULL OUTER JOIN` between the temp heap and the current matview, joined on the required unique index key. That index must use plain column names only — no expression index, no partial `WHERE` — because it defines row identity for the diff.
4. The diff is applied as `DELETE` + `INSERT` (and `UPDATE` for changed non-key columns) against the live matview, inside the refresh transaction. Readers see the old snapshot until commit, then the new one atomically.
5. Commit. `pg_stat_all_tables` shows the churn; autovacuum picks up the dead tuples afterwards.

```
base tables ──run query──► temp heap ──FULL OUTER JOIN on unique key──► diff
                                                                          │
                              live matview ◄──DELETE/INSERT/UPDATE────────┘
```

Cost of step 2 is the full query, always. That is the crux: **`CONCURRENTLY` buys availability, not less work.** It adds a join and row-by-row DML, so with a large change set it is slower and produces more WAL than a plain refresh.

Contrast with genuinely incremental maintenance (Oracle fast refresh):

1. Every DML on a base table appends the changed rowid/PK, DML type, and (with `INCLUDING NEW VALUES`) old and new column values to the materialized view log.
2. At refresh time Oracle reads only rows in the log since the view's last refresh SCN.
3. For an aggregate view it applies delta algebra: `SUM` and `COUNT` are self-maintainable, so inserts add and deletes subtract. `MIN`/`MAX` are *not* self-maintainable under deletes — deleting the current max forces a re-scan of that group, which is why Oracle requires `COUNT(*)` and `COUNT(col)` companions and restricts `MIN`/`MAX` fast refresh to insert-only changes.
4. Applied rows are purged from the log once every dependent view has consumed them.

The same asymmetry explains SQL Server's `COUNT_BIG(*)` requirement, its ban on `AVG` (store `SUM` and `COUNT_BIG` separately), and its ban on outer joins and self-joins — all constructs whose deltas are not derivable from row-local information. Redshift draws the line in the same place: incremental refresh covers `SELECT / FROM / [INNER] JOIN / WHERE / GROUP BY / HAVING` with `SUM, MIN, MAX, AVG, COUNT` and immutable functions, and excludes `LEFT`/`RIGHT`/`FULL OUTER JOIN` and mutable functions such as `RANDOM` or `now()`.

Query rewrite is the third mechanism. SQL Server requires `WITH SCHEMABINDING`, a unique clustered index, deterministic-and-precise expressions, and seven fixed `SET` options (`ANSI_NULLS`, `ANSI_PADDING`, `ANSI_WARNINGS`, `ARITHABORT`, `CONCAT_NULL_YIELDS_NULL`, `QUOTED_IDENTIFIER` all `ON`; `NUMERIC_ROUNDABORT` `OFF`) — at creation, at every DML, and at plan compile time. Automatic matching runs on Enterprise, Azure SQL Database and Managed Instance; on Standard you must write `WITH (NOEXPAND)`. Oracle gates rewrite on `QUERY_REWRITE_INTEGRITY`: `ENFORCED` (fresh data and validated constraints only), `TRUSTED` (believe `RELY` constraints and dimensions), `STALE_TOLERATED` (use the view even when stale).

### Key trade-offs

| Design choice | You gain | You give up |
|---|---|---|
| Synchronous maintenance (indexed view, `ON COMMIT`) | Zero staleness; rewrite always legal | Write amplification on every DML; lock contention; refresh failure aborts the user's transaction |
| Deferred full refresh (PG, ClickHouse RMV) | Simple, any query shape, predictable | Cost ∝ total data, not change rate; staleness window equals refresh period |
| Incremental refresh (Oracle FAST, Redshift, BigQuery) | Cost ∝ change rate | Restricted query grammar; change logs consume storage and add write overhead |
| `CONCURRENTLY` / non-blocking refresh | Readers never blocked | Requires a unique index; slower and more WAL when many rows change |
| Automatic query rewrite | Callers stay unaware; you can add or drop views without touching code | Plan instability; silent staleness; integrity-mode subtleties |
| Bounded-lag contracts (`TARGET_LAG`, `max_staleness`) | Freshness expressed as SLA, engine schedules the work | Continuous background compute cost; freshness is best-effort, not guaranteed |

### Common failure modes

- **Refresh takes longer than the refresh interval** — jobs pile up, staleness grows unbounded; caused by base-table growth outpacing the fixed schedule.
- **`REFRESH CONCURRENTLY` degrades over time** — the diff join is over the whole result set; a unique index over a column with many NULLs enlarges the diff and makes it worse.
- **Silent fallback from FAST to COMPLETE** — `REFRESH FORCE` quietly does a full recompute when a query change breaks fast-refresh eligibility. Verify with `DBMS_MVIEW.EXPLAIN_MVIEW` before shipping.
- **Materialized view log growth** — a dropped or never-refreshed dependent view means log rows are never purged, and the log becomes bigger than the table.
- **Indexed view kills the write path** — a hot OLTP table under several indexed views turns a single-row insert into N index maintenance operations inside the same transaction.
- **Rewrite silently stops firing** — a session connects with `ARITHABORT OFF` (the OLE DB/ODBC default) and SQL Server refuses to use the indexed view; the query plan reverts to base tables with no error.
- **BigQuery staleness cliff** — a `max_staleness` view not refreshed for over 3 days fails at query time because the deltas it needs have aged out of the streaming buffer.
- **Non-deterministic definition** — `now()`, `RANDOM()`, or a mutable UDF in the view body makes the stored result unreproducible and blocks incremental refresh.

## Why

### Why it exists

Because the cost of answering a query and the frequency of asking it are decoupled from the rate at which the answer changes. A query scanning 400M rows to return 200 aggregate rows, asked 5,000 times a day against data that changes 10,000 times a day, does roughly 2×10¹² row-touches when it could do 10⁴. Materialized views convert **repeated read work into amortised write work** — the same trade every cache, index, and CDN makes. What is specific here is that the trade is expressed *declaratively inside the database*, so correctness and freshness are the engine's problem rather than the application's.

### Why it looks the way it does

The obvious alternative design is "maintain everything incrementally, always" — no full refresh, no query restrictions, no staleness. Streaming engines (Materialize, RisingWave, Feldera) do exactly this, and the price is visible: they keep the *operator state* — join hash tables, aggregate accumulators, multiplicity counts — resident for as long as the view exists, and they need a bounded-memory story for every operator. Classic relational engines refused to pay that in the general case, so they split the problem:

- **Restrict the grammar** and maintain incrementally with a small delta log (Oracle fast refresh, SQL Server indexed views). Cheap state, narrow applicability.
- **Allow any grammar** and recompute (PostgreSQL). Zero maintenance state, cost proportional to data.

That split is not laziness; it is a direct consequence of which aggregates are **self-maintainable**. `SUM`/`COUNT` form a group under insert and delete, so a delta suffices. `MIN`/`MAX` and `DISTINCT` do not — you must either re-scan or keep enough state (a multiset per group) to answer "what is the max now that this row is gone". Every restriction list in every vendor's documentation is a restatement of that algebraic fact.

### Why it matters now

The 2026 landscape is converging on the **bounded-lag declarative contract**: you state the freshness you need, the platform schedules the compute. Snowflake dynamic tables (`TARGET_LAG`, minimum 1 minute), BigQuery `max_staleness`, Databricks materialized views, and Redshift AutoMV are all versions of the same idea, and dbt now treats materialized views as a first-class materialization. Two things follow for a working engineer. First, freshness is becoming a *budget line* — background maintenance is metered compute, and an over-eager `TARGET_LAG` is a recurring bill. Second, the boundary between "database materialized view" and "stream processor" is dissolving; the engineering question is no longer which product but which freshness SLA and which query grammar you can live with.

## Open questions / things to verify in practice

- Measure `REFRESH MATERIALIZED VIEW` vs `CONCURRENTLY` on your own data at 1%, 10%, and 90% change ratios. Find the crossover where the diff join stops paying for itself.
- Confirm whether your Oracle view actually fast-refreshes: run `DBMS_MVIEW.EXPLAIN_MVIEW` and check `REFRESH_FAST_AFTER_ANY_DML`, rather than trusting `REFRESH FORCE` to have done the fast path.
- On SQL Server, connect from your real application driver and check the plan for the indexed view. If `ARITHABORT` is `OFF`, matching silently stops and the plan reverts to base tables.
- Instrument staleness as a metric, not a hope: expose `now() - last_refresh` per view and alert on it. Confirm your dashboards' consumers actually accept that number.
- Compare a Snowflake dynamic table at `TARGET_LAG = '1 minute'` against `'1 hour'` on credit consumption for the same query — the delta is usually larger than expected.
- Test whether the optimizer picks your view for queries that *differ slightly* from the definition (extra predicate, coarser `GROUP BY`). Rewrite coverage is where the real value lives, and it is narrower than vendor docs imply.
