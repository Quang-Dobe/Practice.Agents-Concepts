# B-Tree Index — MVP Code

The smallest runnable demo of a B-tree index in PostgreSQL. One SQL script, ~60 lines of actual SQL, the rest is teaching comments.

## What it demonstrates

- **Seq Scan vs Index Scan** — the same three queries run before and after `CREATE INDEX`, with `EXPLAIN (ANALYZE, BUFFERS)` showing the plan change and the buffer-count drop.
- **Range scans on a B-tree** — `created_at` BETWEEN ... uses the index to seek the start key, then walks linked leaves.
- **Composite indexes** — `(user_id, event_type)` handles the joint equality predicate in one seek.
- **The leftmost-prefix rule** — filtering on `event_type` alone cannot use the composite index and falls back to Seq Scan, exactly as the deep dive predicts.

## Prerequisites

PostgreSQL **14+** running locally (any database — the script creates and drops its own table). Quick start: `docker run --rm -d --name pg -e POSTGRES_PASSWORD=pw -p 5432:5432 postgres:16`.

## Run it

```bash
psql -h localhost -U postgres -d postgres -f mvp.sql
```

## What to look for in the output

The script prints three sections, marked with `===` banners:

1. **BEFORE INDEXES** — every plan starts with `Seq Scan on events`, `Buffers: shared hit/read` in the **thousands**, `actual time` in the tens of milliseconds.
2. **AFTER INDEXES** — the same three plans now say `Index Scan using idx_events_*` (or `Bitmap Index Scan`), `Buffers` drops to **single or low double digits**, `actual time` drops to **sub-millisecond**.
3. **LEFTMOST-PREFIX RULE** — the `event_type`-only query goes back to `Seq Scan`. The composite index is **not used**, by design.

The buffer count is the cleanest signal: a B-tree turns thousands of page reads into a handful.

## What to try next

- Change `user_id = 4242` to `user_id < 8000` — planner switches to Seq Scan (too many matches, index loses).
- Add `INCLUDE (created_at)` to `idx_events_user_id` — look for `Index Only Scan` on Q1.
- Bump the insert count from 200k to 2M — the tree gains one level, buffer counts barely change.
