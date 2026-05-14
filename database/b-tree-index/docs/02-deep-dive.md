# B-Tree Index — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

A B-tree index is a self-balancing, on-disk, **n-ary search tree** in which every node is sized to a disk block, every key is held in sorted order within its node, and every leaf sits at the same depth. In practice, every mainstream relational database (PostgreSQL, MySQL/InnoDB, SQL Server, Oracle, SQLite) implements the **B+tree** variant of Bayer & McCreight's original 1972 B-tree: internal nodes carry only *routing keys* (no row data), and the leaves form a **doubly-linked list** for ordered traversal. The "B" stands for *balanced*, not binary; the formal invariant is that every node except the root holds between ⌈m/2⌉ and m children for some order m, which is the property that keeps the tree balanced under arbitrary insert/delete patterns without explicit rebalancing rotations.

### The core building blocks

- **Page (node).** A fixed-size on-disk block. PostgreSQL uses 8 KB by default (configurable at compile time via `--with-blocksize`); InnoDB uses 16 KB; SQL Server uses 8 KB; SQLite uses 4 KB by default. One page = one node = one I/O.
- **Routing key + child pointer.** An internal node stores `[key₁ | ptr₁ | key₂ | ptr₂ | … | keyₙ | ptrₙ₊₁]`. Keys partition the key space; pointers are page numbers of child nodes.
- **Leaf entry.** `[key | TID]` in PostgreSQL, where TID = `(page_no, slot_no)` of the heap tuple. In InnoDB's secondary indexes, the leaf entry is `[key | primary_key]` because the table itself is the clustered B+tree.
- **High key / right link.** Each page records the largest key it covers and a pointer to its right sibling on the same level. This is the **Lehman & Yao** structure that lets readers traverse the tree without locking.
- **Metapage.** A single fixed-location page at the start of the index file pointing to the current root (the root can move when the tree grows in height).
- **Fan-out (branching factor).** The number of children per internal node — typically 100–500 in practice, depending on key width.

### How it relates to the broader landscape

B-trees are the canonical member of the **disk-friendly ordered-index** family. Their siblings are **hash indexes** (faster equality, no ordering), **LSM-trees** (log-structured, write-optimised, used by RocksDB/Cassandra/ScyllaDB), **bitmap indexes** (low-cardinality analytic columns), and **specialized geometric indexes** (R-trees, GiST, SP-GiST). Within the ordered family, the B+tree won out over its theoretical cousins (2-3 trees, red-black trees, skip lists) for one reason: it is shaped around the block-device unit of I/O.

## Where

### Where it runs / lives in the stack

In the **storage engine** layer of a database — below the SQL parser and query planner, above the buffer manager and the OS page cache. The index pages live on disk in the same tablespace as the table data and are read into the buffer pool one page at a time. For OLTP workloads, almost all B-tree traffic is satisfied from RAM: the root and upper internal levels are nearly always cached.

### Where you typically encounter it

- **PostgreSQL** — the default `CREATE INDEX` and every `PRIMARY KEY` / `UNIQUE` constraint.
- **MySQL InnoDB** — the table *itself* is a clustered B+tree on the primary key; every secondary index is another B+tree pointing back by primary key.
- **SQLite** — every table is stored as a B+tree (rowid-keyed) and every index is a separate B-tree.
- **SQL Server / Oracle** — same story; clustered + non-clustered B-trees.
- **MongoDB (WiredTiger)** — B+tree storage engine under the document model.
- **etcd / BoltDB** — embedded key-value stores built on a single B+tree file.

### Ecosystem and tooling

- **For inspection:** `EXPLAIN (ANALYZE, BUFFERS)` in Postgres, `EXPLAIN FORMAT=JSON` in MySQL, `dm_db_index_physical_stats` in SQL Server.
- **For low-level forensics:** `pg_buffercache`, `pageinspect` (Postgres), `innodb_ruby` (Jeremy Cole's InnoDB visualiser).
- **For maintenance:** `REINDEX` (Postgres), `OPTIMIZE TABLE` (MySQL), `ALTER INDEX … REBUILD` (SQL Server).
- **For monitoring bloat / fragmentation:** `pgstattuple`, `pg_repack`, `sys.dm_db_index_physical_stats`.

## When

### When the topic emerged and why

Bayer and McCreight described the B-tree in 1972 at Boeing, targeting disk-resident indexes where a single seek cost milliseconds and in-memory binary trees were therefore catastrophically tall. The B+tree refinement (Knuth, mid-1970s) pushed all data to the leaves and linked them, optimizing the dominant workload: range scans. The Lehman & Yao paper (1981) added right-links and high-keys to make concurrent reads lock-free, which is the variant Postgres still uses today.

### When to use it in a project

Reach for a B-tree index when:

- The column has **high selectivity** — equality matches return < ~5% of rows.
- You need **range queries**, `ORDER BY`, or `BETWEEN`.
- You are enforcing **uniqueness** (it backs `UNIQUE` constraints for free).
- The query uses a **left-anchored** `LIKE 'foo%'`.
- The column is a **foreign key** that participates in joins.
- You need a **composite index** to satisfy multi-column predicates that share a leftmost prefix.

### When NOT to use it

Avoid it when:

- Cardinality is tiny (a boolean) — the planner will scan anyway.
- The table is small enough that a seq scan is already sub-millisecond.
- The workload is write-heavy on the indexed column and reads on it are rare — every insert pays page-split and WAL cost.
- You only need substring search (`LIKE '%foo%'`) — use a GIN/trigram index.
- You need geometric, full-text, or vector similarity — use GiST/GIN/HNSW.

## How

### How it works under the hood

**Node layout.** An internal page holds `[k₁, p₁, k₂, p₂, …, kₙ, pₙ₊₁]` where `kᵢ ≤ kᵢ₊₁` and all keys in subtree `pᵢ` lie in `(kᵢ₋₁, kᵢ]`. A leaf holds `[(k, TID), (k, TID), …]` plus left/right sibling pointers and a high-key. Postgres stores items in a slotted-page format: a fixed header, a growing array of (offset, length) slots from the start, and the actual tuples packed from the end, with free space in the middle.

**Fan-out and height.** For an 8 KB page with ~20-byte routing entries (8-byte bigint key + 6-byte item pointer + slot overhead), an internal node holds roughly 400 children. Heights you should expect:

| Rows         | Height (fan-out ≈ 400) |
|--------------|------------------------|
| 1,000        | 1–2                    |
| 1,000,000    | 2–3                    |
| 100,000,000  | 3–4                    |
| 10,000,000,000 | 4–5                  |

So a point lookup in a 100 M-row table costs ~4 page reads, of which the top 2–3 are essentially always in RAM. The disk-touching cost is **one or two reads** in steady state.

**Lookup (sketch).** Start at the metapage, jump to the root. At each internal node, binary-search the keys to find the child whose subtree contains the search key, follow the pointer. Repeat until a leaf. Binary-search the leaf for the exact key; return TID(s). Total cost: `O(log_b N)` where b is fan-out.

**Insert (sketch).**
1. Descend to the leaf that should hold the new key.
2. If the leaf has space, write the entry in sorted position. Done.
3. If full, **split**: allocate a new leaf, move roughly half the entries to it, link the new leaf into the sibling list, push the **separator key** (smallest of the new right page, or median for plain B-trees) up to the parent.
4. If the parent is also full, recurse the split upward. If the root splits, allocate a new root — this is the only way the tree grows in height.

InnoDB tweaks step 3 for sequential inserts: if `PAGE_LAST_INSERT` indicates monotonic appends, only the new record goes to the right page, leaving the left page ~15/16 full instead of ~1/2. This is why auto-increment primary keys produce dense indexes.

**Delete (sketch).** Find the leaf, mark or remove the entry. If the leaf falls below ⌈m/2⌉ occupancy, **redistribute** with a sibling or **merge** two siblings and pull a key down from the parent. In practice, Postgres avoids eager merging — it keeps half-empty pages around and reclaims them during `VACUUM`. InnoDB merges when occupancy drops below `MERGE_THRESHOLD` (default 50%).

**Why the leaves are linked.** A range scan `WHERE x BETWEEN a AND b` descends once to the leaf containing `a`, then walks `next_leaf` pointers until it sees a key > `b`. No re-traversal, no parent pointers, sequential I/O on the underlying file when pages were allocated contiguously.

**Concurrency.** Postgres implements Lehman & Yao: a reader holds at most one page latch at a time, never two. If a concurrent split moves keys to the right while a reader is descending, the reader notices its search key exceeds the page's high-key and follows the right-link to catch up. Writers take page-level latches plus heavyweight locks only at the leaf being modified. This is why B-tree reads scale linearly with cores.

### Composite indexes and the leftmost prefix rule

An index on `(last_name, first_name, dob)` physically sorts entries first by `last_name`, then by `first_name` within each `last_name`, then by `dob` within each `(last_name, first_name)`. The B-tree can efficiently serve:

- `WHERE last_name = 'Smith'` — yes
- `WHERE last_name = 'Smith' AND first_name = 'Ada'` — yes
- `WHERE last_name = 'Smith' AND dob = '1980-01-01'` — yes for the `last_name` part; `dob` is a filter within that range, not a seek
- `WHERE first_name = 'Ada'` — **no useful seek**: `first_name` is only locally ordered within each `last_name` bucket, so the planner falls back to scan
- `WHERE dob = '1980-01-01'` — same; the index is useless for this predicate

This is not a limitation of the planner; it is a direct consequence of the tree's physical sort order.

### Clustered vs non-clustered, and covering indexes

- **InnoDB / SQL Server clustered index:** the table *is* the B+tree. Leaves hold the full row. One per table. Lookups by primary key avoid any second hop.
- **PostgreSQL:** there is no true clustered index. The table is a heap; every index points into it by TID. The `CLUSTER` command rewrites the heap in index order once, but inserts immediately start to scatter it again.
- **Secondary (non-clustered) index:** the leaf holds `(key, row_pointer)`. Resolving the row needs a second I/O. In InnoDB this is a primary-key lookup; in Postgres it is a heap fetch (potentially randomly placed).
- **Covering index:** includes every column the query needs, so the row fetch is skipped entirely. Postgres 11+ supports `CREATE INDEX … INCLUDE (col)`; SQL Server has had it for two decades.

### Key trade-offs

| Choice              | Win                                       | Loss                                                |
|---------------------|-------------------------------------------|-----------------------------------------------------|
| B+tree vs B-tree    | Range scans via leaf linking; denser internals | Slightly larger total size (keys duplicated in internals) |
| Large page (16 KB)  | Higher fan-out, shallower tree, fewer I/Os | More wasted bandwidth per random read              |
| Clustered           | One I/O for PK lookups                    | Secondary indexes need PK indirection; PK changes are expensive |
| B-tree vs LSM       | Low read amplification, in-place updates  | Higher write amplification (RocksDB ~5–10× lower)   |
| B-tree vs hash      | Range, sort, prefix all free              | Slower point lookup constant factor                 |
| More indexes        | More queries served by an index           | Write amplification, larger working set             |

### Common failure modes

- **Index ignored on low-selectivity predicates.** Planner estimates > ~5–10% of rows match → seq scan wins because random I/O for the heap fetches costs more than streaming the table.
- **Stale statistics.** `ANALYZE` is out of date → planner mis-estimates selectivity and picks the wrong plan.
- **Index bloat.** Long-running transactions hold back `VACUUM`; dead tuples accumulate, leaf pages fragment, index grows 2–10× normal size. Fix: `REINDEX CONCURRENTLY`.
- **Right-edge contention on monotonic keys.** Every insert hits the rightmost leaf — latch contention under high concurrency. Mitigations: UUID v7, hash partitioning, or accept it.
- **Page splits on random-key inserts (UUID v4).** Inserts scatter across the tree, splitting half-full pages everywhere; index ends at ~50–70% fill instead of ~90%.
- **Suffix predicates on composite indexes.** Query uses the second or third column only — index is silently bypassed.
- **`LIKE '%foo'`** — leading wildcard kills the sort order; full index/table scan.
- **Function on the indexed column.** `WHERE lower(email) = …` can't use an index on `email`; needs a functional index on `lower(email)`.

## Why

### Why it exists

The fundamental problem is the **gap between random-access cost and sequential-access cost** on block storage. A spinning disk seek is ~10 ms; an SSD random read is ~100 μs; an in-memory access is ~100 ns. A binary tree of N keys is O(log₂ N) deep — for 1 B keys, that's 30 hops. Thirty random page reads = 3 ms on SSD, 300 ms on disk. A B+tree turns log₂ into log_b with b ≈ 400, collapsing 30 hops to 4. The entire design choice — fat nodes the size of an I/O unit — exists to amortize per-I/O fixed cost over hundreds of keys.

### Why it looks the way it does

Why a B+tree and not a hash table on disk? Hash tables don't preserve order, so range scans and `ORDER BY` require a full scan. Why not a skip list (the in-memory alternative with similar asymptotics)? Skip lists have multiple forward pointers per node and worse cache locality at block granularity — they suit RAM, not disk. Why push data to leaves (B+ vs B)? Plain B-trees store row data in *every* node, which inflates internal nodes and tanks fan-out; routing-only internals keep more of them in cache. Why doubly-link the leaves? So a `WHERE x > 100 ORDER BY x` traverses leaves linearly without paying log N to descend from the root for each step. Why right-links (Lehman & Yao)? So a concurrent split doesn't force readers to hold ancestor latches all the way down — readers escape sideways and stay lock-free.

### Why it matters now

It's 2026 and B-trees still index the world's transactional data. They have not been displaced because the underlying constraint hasn't changed: RAM is still ~100× faster than storage per random access, even on NVMe. LSM-trees have eaten the write-heavy log/timeseries niche (Cassandra, RocksDB), but for **mixed read/write OLTP with secondary indexes and range queries**, B+trees remain dominant — and current research (FAST '22, transparent-compression SSDs) is actually closing the write-amplification gap with LSMs rather than the other way around. Understanding B-trees is the difference between writing `CREATE INDEX` and actually predicting what the planner will do with it.

## Open questions / things to verify in practice

- On your Postgres version, run `EXPLAIN (ANALYZE, BUFFERS)` on a point lookup and confirm the buffer count matches the expected tree height (typically 4–5 for a million-row table including the heap fetch).
- Build the same table twice — once with a monotonic `bigint` PK, once with `uuid v4` — and compare `pg_relation_size('idx')`. Expect ~30–40% size difference.
- Force a low-selectivity query (`WHERE status = 'active'` on a column where 80% match) and verify the planner chooses Seq Scan over Index Scan. Then crank `random_page_cost` down to 1.0 and see the plan flip.
- Create a composite `(a, b, c)` index, then query only on `b`. Confirm `EXPLAIN` shows seq scan or an unrelated index choice.
- Stress-test concurrent inserts on a monotonic PK and watch latch wait events (Postgres: `pg_stat_activity.wait_event = 'BufferPin'` on the rightmost leaf).
- Drop an index, recreate it `CONCURRENTLY`, measure size before/after. Index bloat on a write-heavy table is often surprisingly large.

Sources used while writing this doc:
- [PostgreSQL B-Tree docs](https://www.postgresql.org/docs/current/btree.html)
- [PostgreSQL nbtree README](https://github.com/postgres/postgres/blob/master/src/backend/access/nbtree/README)
- [InnoDB physical structure 8.0](https://dev.mysql.com/doc/refman/8.0/en/innodb-physical-structure.html)
- [Jeremy Cole — B+Tree index structures in InnoDB](https://blog.jcole.us/2013/01/10/btree-index-structures-in-innodb/)
- [Percona — InnoDB Page Merging and Splitting](https://www.percona.com/blog/innodb-page-merging-and-page-splitting/)
- [Lehman & Yao 1981](https://www.csd.uoc.gr/~hy460/pdf/p650-lehman.pdf)
- [Closing the B+-tree vs LSM-tree Write Amplification Gap, FAST '22](https://www.usenix.org/system/files/fast22-qiao.pdf)
