# B-Tree Index — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

Every OLTP backend has a small set of B-tree indexes that hold the system up. In a typical SaaS schema, those are the primary key on `users`, the foreign key from `orders` to `users`, a `(tenant_id, created_at)` composite that powers every listing page, and a partial unique index that enforces "one active subscription per account." Remove any of them and a page that loads in 30 ms loads in 4 seconds.

In an e-commerce or marketplace backend, B-trees are what make pagination on `ORDER BY created_at DESC LIMIT 50` finish in single-digit milliseconds at 50 M rows. In a multi-tenant analytics product, they're the difference between a per-customer report that scans a hot table and one that hits a covering index and never touches the heap.

You also meet B-trees indirectly: every `UNIQUE` constraint, every `PRIMARY KEY`, every foreign key validation in InnoDB. The first place a junior engineer is exposed to "the index is wrong" is usually a slow query review — and 90% of those reviews come down to wrong column order or a predicate that the planner can't use.

## Best practices

### 1. Index foreign keys, but only the ones you actually filter or join on
**Do:** Add a B-tree on every FK that appears in a `WHERE` or `JOIN`, and on FKs the parent deletes/updates cascade through.
**Why:** Postgres does not auto-index FKs (unlike MySQL). A missing FK index turns every `DELETE` on the parent into a full scan on the child, and locks held during the cascade can stall writes for seconds.
**Avoid:** Indexing every FK by reflex. A FK that's never queried just costs writes and storage.

### 2. Put the most selective column first in a composite — except when range queries say otherwise
**Do:** For `WHERE tenant_id = ? AND status = 'open'`, index `(tenant_id, status)`. For `WHERE tenant_id = ? AND created_at > ?`, still lead with `tenant_id` (the equality), then `created_at` (the range).
**Why:** The B-tree is physically sorted left-to-right. Equality on the leading column collapses the search space to a contiguous run; ranges after that are a single leaf walk. Ranges in the middle of a composite kill all columns to their right.
**Avoid:** `(created_at, tenant_id)` for tenant-scoped queries — every tenant's rows are scattered across the whole timeline.

### 3. Respect the leftmost-prefix rule or you're paying for an unused index
**Do:** For an index on `(a, b, c)`, ensure your queries filter on `a`, on `(a, b)`, or on `(a, b, c)`.
**Why:** A query filtering only on `b` cannot seek into the tree — `b` is only locally ordered within each `a` bucket. The planner will silently seq-scan or pick a different index, and your `pg_stat_user_indexes.idx_scan` for that index stays at zero forever.
**Avoid:** Building one fat 5-column index and assuming it covers every subset of those columns.

### 4. Don't wrap indexed columns in functions — use an expression index
**Do:** If you query `WHERE lower(email) = $1`, create `CREATE INDEX ON users (lower(email));`. Same for `WHERE date_trunc('day', created_at) = $1`.
**Why:** An index on `email` is sorted by `email`, not by `lower(email)`. The planner has no way to map one to the other. A function-wrapped column always triggers a seq scan unless an expression index exists.
**Avoid:** Casting in the application to "match" the column type and assuming the index still applies — implicit casts also defeat indexes (e.g. `WHERE int_col = '42'` in some drivers).

### 5. Use partial indexes when a predicate is always-present and narrow
**Do:** `CREATE INDEX ON orders (created_at) WHERE status = 'pending';` if your hot query is "show me pending orders by date."
**Why:** A partial index can be 5–100× smaller than the full one, fits in cache, and the planner can prove it covers the query. Particularly powerful for soft-delete (`WHERE deleted_at IS NULL`) and "one active row" uniqueness (`UNIQUE (user_id) WHERE active`).
**Avoid:** Partial indexes whose predicate doesn't match what you write in the query verbatim — the planner won't use it.

### 6. Skip indexes on low-cardinality columns; combine them into composites instead
**Do:** Instead of `CREATE INDEX ON orders (status)`, build `CREATE INDEX ON orders (status, created_at)` or make the index partial on the rare value.
**Why:** A boolean or 3-value status column has tens of millions of duplicates. The planner correctly decides a seq scan is cheaper than 10 M random heap fetches, so the index is dead weight that still costs every write.
**Avoid:** Indexing `is_active`, `is_deleted`, `gender`, `country` standalone. They earn their place only as the *trailing* part of a composite, or inside a partial index's `WHERE`.

### 7. Reach for `INCLUDE` when one query is hot and one extra column makes it index-only
**Do:** `CREATE INDEX ON sales (customer_id, sold_at) INCLUDE (amount);` if your hot query is `SELECT amount FROM sales WHERE customer_id = ? AND sold_at > ?`.
**Why:** The included column lives in the leaf but isn't part of the sort key, so writes don't reorder the tree. The plan flips from Index Scan + heap fetch to **Index Only Scan**, halving the I/O and skipping the visibility check on most pages.
**Avoid:** Stuffing every selected column into `INCLUDE`. Wide leaves lower fan-out, raise tree height, and bloat the index. Include 1–2 small columns, not 8.

### 8. Pick monotonic primary keys; if you need UUIDs, use v7 or ULID, not v4
**Do:** Default to `bigint identity` / `bigserial`. If you need a globally unique opaque ID, use **UUIDv7**, ULID, or a Snowflake-style ID — all time-ordered.
**Why:** UUIDv4 inserts land at random positions in the clustered B-tree. Pages split everywhere, the index stabilizes around 60–70% full instead of 90%, and benchmarks show 2–5× slower insert throughput and 5–10× more I/O per insert from cascading splits. UUIDv7 keeps inserts at the right edge — same write pattern as a sequence.
**Avoid:** Switching to UUIDv4 PKs "for security" — they expose nothing v7 doesn't, and they wreck your write path.

### 9. Read EXPLAIN before you trust the index
**Do:** Run `EXPLAIN (ANALYZE, BUFFERS)` and learn the four shapes:
- **Index Scan** — descend the tree, fetch matching rows from the heap. Good for high-selectivity equality / small ranges.
- **Index Only Scan** — index covers every column the query needs. Best case.
- **Bitmap Index Scan + Bitmap Heap Scan** — multiple indexes or a medium-selectivity predicate; planner builds a bitmap of TIDs and reads the heap in physical order. Often optimal for 0.5–5% selectivity.
- **Seq Scan** — full table read. Correct choice when > ~10% of rows match or stats are stale.
**Why:** Adding an index doesn't mean it will be used. The planner picks based on `pg_statistic`; if `ANALYZE` hasn't run recently or `random_page_cost` is mistuned for your SSD, it picks wrong.
**Avoid:** "I created the index, query should be fast now" without verifying the plan.

### 10. Treat index bloat as a real maintenance task
**Do:** Monitor index size vs. table size with `pgstattuple`. Use `REINDEX INDEX CONCURRENTLY` or `pg_repack` when bloat passes ~30%. In InnoDB, use `OPTIMIZE TABLE` during maintenance windows.
**Why:** A long-running transaction blocks `VACUUM`; dead index entries pile up; a 2 GB index becomes a 6 GB index; the working set no longer fits in `shared_buffers`; latencies climb 10×. Bloat is silent until it isn't.
**Avoid:** Plain `REINDEX` on a production table — it takes an `ACCESS EXCLUSIVE` lock. Always use `CONCURRENTLY` on Postgres 12+.

### 11. Match index `ORDER BY` direction when sorts are paginated
**Do:** For `ORDER BY created_at DESC LIMIT 50`, an ascending index on `created_at` is fine (Postgres scans backwards). But for `ORDER BY (a ASC, b DESC)`, you need `CREATE INDEX ON t (a ASC, b DESC)` to avoid a sort step.
**Why:** Mixed-direction ordering is the one case backward scans don't cover. Without the matching index, the planner adds a Sort node, which spills to disk on large result sets and breaks keyset pagination.
**Avoid:** Designing the index without looking at the actual `ORDER BY` clause.

### 12. Tune `fillfactor` on heavily-updated indexes, leave defaults elsewhere
**Do:** Postgres defaults to fillfactor 90 for B-tree indexes (10% slack per page). For a table with very frequent updates on indexed columns, lower it to 70–80 so HOT updates stay on-page and new entries fit without splits.
**Why:** Lower fillfactor trades storage for fewer page splits and less bloat over time. Higher fillfactor (e.g. 100 for append-only tables) packs leaves tighter and shrinks the index.
**Avoid:** Cargo-culting fillfactor 70 on every table. For append-only or read-mostly tables, you're just wasting space.

## Anti-patterns to recognize

- **The "index everything" reflex.** Every column gets its own index "just in case." Writes slow down linearly with index count, the working set blows past RAM, and most of those indexes are never read. Better: index from observed `pg_stat_statements` queries, not speculation.
- **Composite index whose order matches the schema, not the query.** Engineers list columns in `(created_at, tenant_id)` because that's the order they appear in the table. Queries filter `WHERE tenant_id = ?` and the index is useless. Order columns by query shape, not table shape.
- **Indexing a column with a `BEFORE INSERT` trigger that rewrites it.** The trigger reshapes the value at insert time and the WHERE clause uses the original input. Classic timezone / collation pitfall. Index the post-trigger form.
- **`SELECT *` over a covering index.** The index covers six of seven columns; the seventh forces a heap fetch and the query plan silently degrades from Index Only Scan to Index Scan. Always project explicit columns when relying on `INCLUDE`.
- **Indexed boolean as primary filter.** `WHERE is_active = true` on a table where 95% of rows are active. The planner ignores the index. Either flip to a partial index on the rare value or drop the index entirely.
- **Re-adding an index that's already redundant.** An index on `(a, b)` already serves queries that filter on `a` alone — there's no need for a separate index on `(a)`. Duplicate-by-prefix indexes are pure write-amplification.
- **Trusting `idx_scan > 0` as "the index is needed."** Postgres counts a single use, ever, as a hit. Look at `idx_scan` *relative to* table writes and to other indexes. `pg_stat_user_indexes` plus a time series tells the real story.

## Real-world usage patterns

**Multi-tenant SaaS list views.** A B2B product with ~10K tenants and 500 M total rows across all of them. Every API list endpoint filters by `tenant_id` first. The canonical index pattern is `(tenant_id, <sort column>) INCLUDE (<2–3 hot columns>)` — one per dominant query shape. Lesson: in a multi-tenant system, `tenant_id` is the leading column of nearly every useful index, full stop. Without it, one noisy tenant's queries blow up cache for everyone else.

**Keyset pagination on a feed.** A social feed at ~10K writes/sec uses `WHERE (created_at, id) < ($cursor_ts, $cursor_id) ORDER BY created_at DESC, id DESC LIMIT 50` backed by `(created_at DESC, id DESC)`. Lesson: classic `OFFSET 10000` paging gets quadratically slower; keyset pagination plus a matching descending composite index is flat. The id tiebreaker is what makes it stable across duplicate timestamps.

**Soft-delete with partial unique index.** An auth service enforcing "one active email per user" while keeping deleted rows: `CREATE UNIQUE INDEX ON users (email) WHERE deleted_at IS NULL`. Lesson: partial unique indexes solve constraints that no other tool expresses cleanly — way better than triggers, way safer than application-side checks.

**Time-series append.** An ingestion table at 50K inserts/sec keyed on a Snowflake ID. The clustered B+tree fills its rightmost leaf, splits cleanly to ~15/16 full (InnoDB's monotonic-insert optimization), and the index stays dense at ~95%. Lesson: monotonic keys aren't just about avoiding fragmentation — they unlock storage-engine fast-paths that random keys never trigger.

**The covering-index rescue.** A reporting query joined three tables and timed out at 8 seconds. Adding `(account_id, period) INCLUDE (revenue, cost)` to the fact table flipped the plan to Index Only Scan; query dropped to 40 ms. Lesson: covering indexes are the single biggest lever for read-heavy OLAP-ish queries on an OLTP database — when you can't move the workload to a warehouse, INCLUDE buys you another year.

## Operational checklist

- **Stats freshness:** Is `autovacuum` running? When did `ANALYZE` last touch the hot tables? Stale stats produce wrong plans far more often than bad indexes do.
- **Plan verification:** For every new or changed index, attach the `EXPLAIN (ANALYZE, BUFFERS)` output to the PR. Reviewer should see Index Scan / Index Only Scan, not Seq Scan.
- **Unused-index alert:** Do you have a weekly job listing indexes with `idx_scan = 0` over the last 30 days? They are pure write-tax — drop them.
- **Bloat monitoring:** Is index size vs. live tuple ratio tracked? Alert when an index is >2× expected size.
- **Lock-safe migrations:** Is every `CREATE INDEX` in production `CONCURRENTLY`? Same for `REINDEX`? Anything else takes an exclusive lock and pages on-call.
- **PK choice review:** New tables get reviewed for `bigint` vs UUID. If UUID, is it v7/ULID, not v4?
- **Cost ceiling:** Each new index adds ~10–20% write overhead. Are you at >10 indexes on a hot OLTP table? Justify it.
- **Onboarding:** Can a new engineer read `pg_stat_user_indexes` and `EXPLAIN ANALYZE` output on day one? If not, the first slow-query ticket teaches them painfully.
- **Backup-and-restore size sanity:** Indexes can be 50–70% of total relation size. Restore time scales with them. Know the number.

## How this topic typically evolves in a codebase

Most projects start with one index per query — engineers add a B-tree the first time something is slow, and the database happily obliges. By year two there are 8–12 indexes on the hottest table, half of them overlapping, and writes have visibly slowed. Nobody knows which indexes are safe to drop.

The painful migration point is usually one of three things: switching the primary key strategy (UUIDv4 → v7 or ULID, requiring a rewrite of every secondary index), introducing partitioning (B-trees are local to each partition, which changes uniqueness semantics), or moving reports off the OLTP store entirely. The first one is the worst — it touches every foreign key in the schema.

Mature teams converge on a small workflow: query patterns come from `pg_stat_statements`, indexes are designed from those patterns (not guessed), and there's a quarterly review that drops unused ones. The teams that stay healthy treat indexes as code — versioned, reviewed, and tied to a specific business query — not as a performance band-aid sprinkled on after the fact.

## Further reading

- [PostgreSQL Indexes and Index-Only Scans](https://www.postgresql.org/docs/current/indexes-index-only-scans.html) — primary source for covering indexes and the visibility-map interaction.
- [Use The Index, Luke](https://use-the-index-luke.com/) — Markus Winand's free book; the canonical reference for index design across Postgres, MySQL, Oracle, SQL Server.
- [Jeremy Cole — InnoDB B+Tree Index Structures](https://blog.jcole.us/2013/01/10/btree-index-structures-in-innodb/) — visualizations of how clustered indexes look on disk; changes how you reason about MySQL.
- [Cybertec — What is fillfactor and how does it affect performance](https://www.cybertec-postgresql.com/en/what-is-fillfactor-and-how-does-it-affect-postgresql-performance/) — concrete numbers on fillfactor tradeoffs in real workloads.
- [PostgreSQL UUID Performance: v4 vs v7 benchmark](https://dev.to/umangsinha12/postgresql-uuid-performance-benchmarking-random-v4-and-time-based-v7-uuids-n9b) — measured insert throughput and index size differences; the case for v7 in one chart.
- [pgDash — Notes on PostgreSQL B-Tree Indexes](https://pgdash.io/blog/postgres-btree-index.html) — operator-oriented walkthrough of bloat, partial indexes, and INCLUDE in production.
