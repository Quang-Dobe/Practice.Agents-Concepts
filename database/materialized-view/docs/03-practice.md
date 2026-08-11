# Materialized View — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a B2B SaaS backend, the materialized view is almost always the thing behind the "Analytics" tab. Someone wrote a `GROUP BY tenant_id, day` over the events table, it was fine at 2 million rows, and at 300 million it started timing out the API gateway. The fix that ships in week one is `CREATE MATERIALIZED VIEW` plus a `pg_cron` job. The consequence that shows up in month six is a 40-minute refresh on a 15-minute schedule.

In a data platform, you meet it as the middle layer of a dbt project — `materialized='materialized_view'` on a staging model, or a Snowflake dynamic table with `TARGET_LAG = '1 hour'`. Here it competes directly with "just build a table with an incremental dbt model." The decision is less about performance and more about who owns the refresh: the engine or your orchestrator.

In an OLTP-heavy system (payments, order management), you meet it in its synchronous form — a SQL Server indexed view or an Oracle `ON COMMIT` MV — usually added by a DBA to rescue a reporting query, and usually discovered later by the team wondering why single-row inserts got 4× slower.

And you meet it as a *decoy*: a proposal in a design doc where the real answer was a covering index, a Redis cache, or a nightly summary table with an explicit ETL job.

## Best practices

### 1. Write the staleness budget into the view's name or comment

**Do:** State the freshness contract explicitly — `mv_revenue_daily_15m`, or a `COMMENT ON MATERIALIZED VIEW ... IS 'refresh: 15m; consumer: finance dashboard; owner: #team-data'`. Expose `last_refresh` to consumers.
**Why:** Staleness disputes are the number one MV incident, and they are always "nobody wrote down what fresh means." A support ticket saying "the dashboard is wrong" costs an hour of on-call time to resolve into "it is 12 minutes behind, as designed."
**Avoid:** Treating refresh cadence as an implementation detail hidden in a cron file.

### 2. Measure full refresh duration against the refresh interval, with headroom

**Do:** Alert when `refresh_duration > 0.5 × refresh_interval`. Track the ratio over time, not the raw duration.
**Why:** Refresh cost grows with total data; the interval is fixed. The failure is not gradual — you sit at 30% utilization for a year, then base-table growth crosses the line and refreshes overlap, queue, and staleness grows without bound. The 50% threshold gives you a quarter of warning.
**Avoid:** Alerting only on "refresh failed." A refresh that takes 3× the interval never fails; it just silently stops being useful.

### 3. Choose `CONCURRENTLY` on change ratio, not on principle

**Do:** Benchmark plain `REFRESH` vs `CONCURRENTLY` at your real change ratio. Below roughly 10–20% row churn, `CONCURRENTLY` usually wins on availability at acceptable cost. Near full turnover, plain refresh is faster and produces far less WAL.
**Why:** `CONCURRENTLY` still runs the whole query, then adds a `FULL OUTER JOIN` and row-by-row DML. At 90% churn you pay the full recompute *plus* a diff, and you generate dead tuples for autovacuum to chase.
**Avoid:** Adding `CONCURRENTLY` reflexively because "it doesn't block readers" — on a high-churn view it turns a 2-minute blocking refresh into a 20-minute bloat generator.

### 4. Design the unique index before you write the view

**Do:** Pick the natural grain key (`tenant_id, bucket_date`), guarantee it is unique in the query, and create a plain-column unique index — no expressions, no partial `WHERE`.
**Why:** PostgreSQL's `CONCURRENTLY` diff uses that index as row identity. Without it, `CONCURRENTLY` is illegal; with a bad one (nullable columns, wrong grain), the diff degenerates into full delete/insert and bloat accelerates.
**Avoid:** Bolting a `row_number()` surrogate key on afterwards — it changes on every recompute, so every row diffs as changed.

### 5. Tune autovacuum on the view itself

**Do:** Set per-table `autovacuum_vacuum_scale_factor = 0.01` (or a fixed threshold) on frequently-refreshed concurrent MVs, and watch `n_dead_tup` in `pg_stat_all_tables`.
**Why:** `REFRESH CONCURRENTLY` is `DELETE` + `INSERT` under the hood. Default autovacuum settings (20% of table) are tuned for OLTP churn, not for a table that rewrites 10% of itself every 5 minutes. The observed failure is a view whose on-disk size grows monotonically while row count stays flat, until reads get slower than the original query.
**Avoid:** Assuming the engine cleans up because "it's a view."

### 6. Verify incremental refresh actually happens — do not trust the keyword

**Do:** On Oracle, run `DBMS_MVIEW.EXPLAIN_MVIEW` and check `REFRESH_FAST_AFTER_ANY_DML`. On Redshift/BigQuery, check the refresh history for incremental vs full. Add this to the PR checklist for any change to the view body.
**Why:** `REFRESH FORCE` silently falls back to `COMPLETE` when a query edit breaks eligibility (adding an outer join, a `MIN`, a mutable function). Nothing errors. Your 3-second refresh becomes a 20-minute one and you find out from the cost dashboard.
**Avoid:** Reviewing an MV definition change as if it were an ordinary SQL diff.

### 7. Keep the view definition deterministic

**Do:** Push `now()`, `current_date`, and `RANDOM()` out of the view body. Materialize by absolute time bucket (`date_trunc('day', created_at)`), filter to "last 30 days" at read time.
**Why:** A `WHERE created_at > now() - interval '30 days'` inside an MV means the stored result silently means something different depending on when it was refreshed, and it disqualifies incremental maintenance on every engine that offers it.
**Avoid:** Relative-time predicates in the definition — they are the single most common reason a view cannot be incrementally maintained.

### 8. Cap the fan-out: never materialize per-user results

**Do:** Materialize the shared aggregate at the coarsest grain that still answers the question, then filter and re-aggregate at read time.
**Why:** One MV per tenant, or a `GROUP BY user_id` over a 5-million-user table, turns refresh cost and storage into a function of your customer count. Teams hit engine object limits (Redshift AutoMV caps at 200 per database) or refresh-scheduler saturation long before they hit a storage limit.
**Avoid:** "We'll just create the view dynamically per customer."

### 9. Refresh dependent views in dependency order, in one job

**Do:** Model the MV DAG explicitly (dbt, or a hand-written ordered refresh procedure) and refresh leaves last. Fail the whole job if a parent fails.
**Why:** Independent cron entries produce **skew**: `mv_orders` refreshes at :00, `mv_order_lines` at :05, and for five minutes the two disagree. Finance reconciliation catches this and does not trust the platform again.
**Avoid:** One `pg_cron` line per view, all on `*/15`.

### 10. Guard the synchronous variants behind a write-latency test

**Do:** Before adding a SQL Server indexed view or an Oracle `ON COMMIT` MV over an OLTP table, load-test the write path with and without it. Record p99 insert latency in the PR.
**Why:** Every DML on the base table now maintains the view's clustered index inside the user's transaction. Microsoft's own guidance is that DML against a table under many or complex indexed views can degrade severely. The blast radius is the checkout path, not a dashboard.
**Avoid:** Adding a synchronous view "because it's always fresh" without measuring who pays for that freshness.

### 11. Prefer a cache or a plain table when the engine gives you nothing extra

**Do:** Use an MV when you want the *engine* to own the derivation and (where supported) query rewrite. Use a plain table + explicit ETL when you need partial refresh, backfills, late-arriving-data handling, or per-partition retries. Use Redis when the access pattern is keyed lookup with a TTL and no joins.
**Why:** PostgreSQL MVs give you all-or-nothing refresh. The moment someone asks "can we just re-refresh yesterday?", you are rewriting it as a partitioned table anyway — and that rewrite is more expensive after six months of dependencies.
**Avoid:** Choosing MV because the syntax is one line shorter than the ETL job.

## Anti-patterns to recognize

- **The refresh-every-minute reflex**: someone sets `*/1 * * * *` on a view that takes 90 seconds. Refreshes overlap and pile up (or serialize on the exclusive lock), CPU stays pinned, and staleness is *worse* than a 15-minute schedule. Set the interval to at least 3× measured p95 refresh duration, and enforce mutual exclusion with an advisory lock.

- **MV as a cache**: using a materialized view for keyed lookups (`WHERE user_id = ?`) because "it's faster." A full recompute to serve point reads is the most expensive cache invalidation strategy ever devised. Use an index, or a real cache with a TTL.

- **Stacked views with no DAG**: `mv_c` selects from `mv_b` selects from `mv_a`, each on its own schedule. Total staleness is the *sum* of the levels, and nobody can say what the number is. Flatten to two levels or orchestrate the DAG explicitly.

- **The `SELECT *` view**: materializing wide rows with no aggregation. There is no reduction ratio, so you have doubled storage and write amplification for a marginal read gain. If the query does not collapse rows by at least 10×, an index is almost certainly the right tool.

- **Silent rewrite loss**: on SQL Server, the app connects via a driver with `ARITHABORT OFF`, so the optimizer refuses the indexed view and quietly scans base tables. Nothing errors; latency just triples after a driver upgrade. Test plans from the real application connection, not from SSMS.

- **Orphaned MV logs**: an Oracle materialized view log whose dependent view was dropped or has not refreshed in months. Log rows are never purged, and the log grows larger than the base table it journals. Audit `USER_MVIEW_LOGS` against live views quarterly.

- **The unbounded backfill refresh**: a full refresh over all history that used to take 10 minutes and now takes 6 hours because the view has no time bound. Partition the underlying data and materialize per-period, or move to a rolling window with an archive table.

## Real-world usage patterns

**Usage-metering dashboard in a multi-tenant SaaS (PostgreSQL, ~200 GB events table).** A daily rollup MV keyed on `(tenant_id, usage_date)` refreshes every 10 minutes with `CONCURRENTLY`; the API filters it by tenant at read time. The non-obvious lesson: the win came less from the aggregation and more from *shrinking the working set* — the MV fit in shared buffers, the base table never did, so the p99 improvement was far larger than the row-count ratio predicted.

**Financial reporting warehouse (Snowflake dynamic tables).** A chain of dynamic tables replaced a nightly dbt job when the business asked for hourly numbers. The team started at `TARGET_LAG = '1 minute'` in staging and moved to `'1 hour'` in production after the first bill. The lesson: freshness is a metered subscription, not a one-time cost, and the credit curve is steeply non-linear below ~15 minutes of lag. Dynamic tables also give you explicit warehouse attribution, which classic Snowflake MVs do not — that visibility alone is often worth the switch.

**E-commerce product search ranking (Redshift/BigQuery, incremental refresh).** A ranking projection joining orders, reviews, and inventory is materialized and refreshed incrementally. The lesson: the *query grammar restriction drove the data model*. The team removed a `LEFT JOIN` to an optional attributes table (replacing it with a nullable denormalized column upstream) purely so the view stayed incrementally maintainable. Refresh cost dropped from minutes to seconds.

**Legacy ERP reporting (Oracle, fast refresh + query rewrite).** Hundreds of pre-existing reports were sped up without touching a single report, by adding aggregate MVs and letting `QUERY_REWRITE_INTEGRITY = TRUSTED` redirect them. The lesson: rewrite coverage is narrower than the docs imply — reports whose `GROUP BY` was slightly finer than the MV's grain never matched, and finding them required `DBMS_MVIEW.EXPLAIN_REWRITE` per report, not faith.

## Operational checklist

- **Freshness metric**: is `now() - last_refresh` exported per view, with an alert threshold that matches the documented staleness budget?
- **Refresh headroom**: is refresh p95 duration under 50% of the refresh interval, and is that ratio trended?
- **Overlap protection**: does the refresh job take an advisory/named lock so two runs cannot stack?
- **Failure behaviour**: when a refresh fails, do consumers keep serving stale-but-valid data (yes, for deferred MVs) or does a user transaction abort (possible for synchronous ones)? Is that path tested?
- **Bloat**: for `CONCURRENTLY` refreshed views, is per-table autovacuum tuned, and is on-disk size trended against row count?
- **Incremental verification**: does CI or the PR checklist prove the view still refreshes incrementally after a definition change?
- **Security**: does the MV bypass row-level security or column masking on the base tables? An MV stores rows as the *definer* saw them — check that per-tenant isolation is re-applied at read time.
- **Cost**: what is the monthly compute cost of this view's refresh, and would doubling the freshness double the bill?
- **Onboarding**: can a new engineer find, in one place, the view's owner, its staleness contract, its consumers, and how to trigger a manual refresh safely?

## How this topic typically evolves in a codebase

Teams start with one MV, created reactively after a slow-query alert, refreshed by a cron entry someone added by hand. It works, so a second appears, then a fifth. Around view number five the refresh jobs start colliding, the schedules drift apart, and the team notices that two dashboards disagree. This is the first migration point: cron entries get replaced by an orchestrated DAG (dbt, Airflow, or a single ordered stored procedure), and the views acquire owners and documented staleness budgets.

The second, more painful migration point comes when a full refresh no longer fits its window. There are only three ways out, and all of them are rewrites: partition the base data and materialize per-period; move to an engine with real incremental maintenance (Oracle fast refresh, Redshift, BigQuery, `pg_ivm`, TimescaleDB continuous aggregates); or abandon the MV abstraction for a hand-maintained table with explicit change capture. The third option is the most common in PostgreSQL shops, and it is worth anticipating: if you can see the moment coming, design the view's grain and key so the table version is a mechanical translation rather than a redesign.

The end state at scale is usually a small number of coarse, incrementally-maintained views with published freshness SLAs, plus read-time filtering — rather than many fine-grained views. The trend across platforms points the same way: you declare the lag you need and the engine schedules the work, which means the engineering skill shifts from writing refresh jobs to negotiating freshness against cost.

## Further reading

- [PostgreSQL: `REFRESH MATERIALIZED VIEW`](https://www.postgresql.org/docs/current/sql-refreshmaterializedview.html) — short, and the `CONCURRENTLY` caveats about the unique index and relative cost are the ones people skip.
- [Cybertec: Creating and refreshing materialized views in PostgreSQL](https://www.cybertec-postgresql.com/en/creating-and-refreshing-materialized-views-in-postgresql/) — the clearest practical treatment of the bloat/VACUUM consequences of concurrent refresh.
- [Gupta, Mumick & Subrahmanian, *Maintaining Views Incrementally* (SIGMOD 1993)](https://dl.acm.org/doi/10.1145/170035.170078) — why `SUM` is incrementally maintainable and `MIN`/`MAX` are not. Every vendor's restriction list is a restatement of this paper.
- [DBSP: Automatic Incremental View Maintenance (PVLDB 16:1601, 2023)](https://www.vldb.org/pvldb/vol16/p1601-budiu.pdf) — the modern generalization, and the theory behind Feldera and the current wave of streaming MV engines.
- [Snowflake: Understanding costs for dynamic tables](https://docs.snowflake.com/en/user-guide/dynamic-tables-cost) — read before choosing a `TARGET_LAG`; it makes the freshness-as-recurring-bill trade-off explicit.
- [Microsoft: Create indexed views](https://learn.microsoft.com/en-us/sql/relational-databases/views/create-indexed-views) — the `SET`-option requirements and the write-path warnings, which is where indexed-view incidents come from.
