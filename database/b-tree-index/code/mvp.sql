-- =============================================================================
-- B-Tree Index — MVP
-- =============================================================================
-- What this file proves, end-to-end, in a single psql run:
--   1. Without an index, Postgres has to read the whole table (Seq Scan)
--      for equality, range, and composite predicates.
--   2. Adding the right B-tree index flips the same queries to Index Scan
--      (or Bitmap Index Scan), with dramatically fewer buffers and
--      lower actual time.
--   3. A composite (user_id, event_type) index only helps queries that
--      include user_id (the LEFTMOST column). A query on event_type alone
--      cannot seek the tree and goes back to Seq Scan — the leftmost-prefix
--      rule, made concrete.
--
-- Run with:   psql -f mvp.sql -d <some_db>
--
-- The script is idempotent: it drops and re-creates everything from scratch.
-- =============================================================================

-- Clean slate every run. CASCADE handles any leftover indexes/constraints.
DROP TABLE IF EXISTS events CASCADE;

-- A realistic-ish events table. Enough columns that composite indexing is
-- meaningful, not so many that the demo gets noisy.
--
--   user_id     — high-cardinality foreign-key-shaped column (~10K distinct)
--   event_type  — low-cardinality (5 values), classic "good as trailing column
--                 in a composite, bad on its own"
--   created_at  — monotonic timestamp, classic range-scan target
--   payload     — wide-ish text so the heap is meaningfully larger than the index
CREATE TABLE events (
    id          bigserial PRIMARY KEY,
    user_id     bigint      NOT NULL,
    event_type  text        NOT NULL,
    created_at  timestamptz NOT NULL,
    payload     text        NOT NULL
);

-- ---------------------------------------------------------------------------
-- Seed 200,000 rows.
--   user_id     = i % 10000          → ~10K distinct users, ~20 rows each
--   event_type  = one of 5 values    → low cardinality, ~40K rows per type
--   created_at  = spread across ~2 years backwards from now
--   payload     = enough bytes to keep the heap visibly fatter than the index
-- generate_series lets us do this in one statement; INSERT is faster than
-- a procedural loop and the data distribution is deterministic.
-- ---------------------------------------------------------------------------
INSERT INTO events (user_id, event_type, created_at, payload)
SELECT
    (i % 10000)::bigint                              AS user_id,
    (ARRAY['click','view','purchase','login','logout'])[(i % 5) + 1] AS event_type,
    now() - (i * interval '5 minutes')               AS created_at,
    repeat('x', 80)                                  AS payload
FROM generate_series(1, 200000) AS g(i);

-- ANALYZE is mandatory: without fresh stats the planner uses defaults and
-- the "before" plans below would not be representative.
ANALYZE events;

-- ===========================================================================
-- PART 1 — BEFORE any non-PK index exists.
--
-- The only index right now is the implicit one on PRIMARY KEY (id).
-- None of these three queries can use it, so all three should Seq Scan
-- the whole heap. Watch the "Buffers: shared hit/read" line and the
-- "actual time" — those are the numbers we want to shrink.
-- ===========================================================================

\echo
\echo '=== BEFORE INDEXES ==='
\echo

-- Q1: equality on a high-selectivity column.
-- Expected plan: Seq Scan on events, ~200K rows scanned to return ~20.
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, created_at
FROM events
WHERE user_id = 4242;

-- Q2: a range on created_at (last 7 days). Expected: Seq Scan again.
-- Even though created_at is naturally sorted by insert order in the heap,
-- Postgres has no index telling it where to start, so it reads every page.
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, user_id, event_type
FROM events
WHERE created_at >= now() - interval '7 days'
  AND created_at <  now();

-- Q3: composite filter, equality on both columns. Same story — Seq Scan.
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, created_at
FROM events
WHERE user_id = 4242
  AND event_type = 'purchase';

-- ===========================================================================
-- PART 2 — Create the B-tree indexes.
--
-- Three indexes, each targeting one of the queries above:
--   - idx_events_user_id        → Q1 (equality on user_id)
--   - idx_events_created_at     → Q2 (range on created_at)
--   - idx_events_user_event     → Q3 (composite, leading column user_id)
--
-- These are all plain B-trees — Postgres' default and the topic of this demo.
-- USING btree is implicit; written explicitly here for teaching clarity.
-- ===========================================================================

CREATE INDEX idx_events_user_id     ON events USING btree (user_id);
CREATE INDEX idx_events_created_at  ON events USING btree (created_at);
CREATE INDEX idx_events_user_event  ON events USING btree (user_id, event_type);

-- Re-ANALYZE so the planner sees the new indexes' stats immediately.
ANALYZE events;

-- ===========================================================================
-- PART 3 — AFTER indexes exist. Same three queries.
--
-- What to look for in each plan:
--   - The node type changes from "Seq Scan" to "Index Scan" or
--     "Bitmap Index Scan + Bitmap Heap Scan".
--   - "Buffers: shared hit" drops by orders of magnitude (from thousands
--     of pages down to single digits or low tens).
--   - "actual time" drops to sub-millisecond on most local hardware.
-- ===========================================================================

\echo
\echo '=== AFTER INDEXES ==='
\echo

-- Q1 again. Expected: Index Scan using idx_events_user_id.
-- ~20 matching rows, ~4 buffer reads (the tree height + heap fetch).
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, created_at
FROM events
WHERE user_id = 4242;

-- Q2 again. Expected: Index Scan (or Bitmap Index Scan) using
-- idx_events_created_at. The descending scan starts at the leftmost
-- leaf >= "now - 7 days" and walks the linked leaves rightward.
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, user_id, event_type
FROM events
WHERE created_at >= now() - interval '7 days'
  AND created_at <  now();

-- Q3 again. Expected: Index Scan using idx_events_user_event.
-- The composite index lets the planner seek to (user_id=4242, event_type='purchase')
-- directly — no filtering inside the heap.
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, created_at
FROM events
WHERE user_id = 4242
  AND event_type = 'purchase';

-- ===========================================================================
-- PART 4 — The leftmost-prefix rule, made concrete.
--
-- The composite index on (user_id, event_type) is physically sorted FIRST
-- by user_id, THEN by event_type within each user_id. So event_type is only
-- LOCALLY ordered — within each user_id bucket. There is no global ordering
-- on event_type that the tree can seek into.
--
-- Therefore: a query filtering ONLY on event_type CANNOT use this index
-- as a seek. Expected plan: Seq Scan (the planner correctly ignores the
-- composite). This is not a planner bug — it is a direct consequence of
-- how B-trees physically store keys.
-- ===========================================================================

\echo
\echo '=== LEFTMOST-PREFIX RULE: event_type alone ==='
\echo

-- Expected: Seq Scan on events. The composite index is silently bypassed.
-- (Also note: event_type has only 5 distinct values, so even a dedicated
-- single-column index would lose to Seq Scan on selectivity grounds —
-- but that's a separate lesson; see 03-practice.md, best practice #6.)
EXPLAIN (ANALYZE, BUFFERS)
SELECT count(*)
FROM events
WHERE event_type = 'purchase';
