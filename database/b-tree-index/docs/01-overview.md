# B-Tree Index — Overview

> A B-tree index is a sorted, shallow, wide tree the database keeps next to your table so it can find rows by key in a handful of hops instead of scanning every page.

## The 30-second version

When you tell a database `WHERE user_id = 42`, the naive answer is "read every row and check." That works for a hundred rows and dies at a hundred million. A B-tree index is a separate on-disk structure — a tree of sorted keys with pointers to the actual rows — that turns that linear scan into a logarithmic walk. It is the default index type in PostgreSQL, MySQL/InnoDB, SQL Server, Oracle, and SQLite for a reason: it is fast for equality lookups, fast for range scans, and stays balanced as the table grows. Almost every `CREATE INDEX` you have ever written, unless you explicitly said otherwise, built a B-tree.

## The mental model

Picture a phone book, but a weird one. The first page is not names — it is a short list of *signposts*: "names starting A–F live in volume 1, G–M in volume 2, N–S in volume 3, T–Z in volume 4." You open volume 2, and its first page is again signposts: "G–H in section 2a, I–J in 2b…" You keep descending. After three or four hops you are on a single page with maybe a few hundred names, sorted, and you scan that one page.

That is a B-tree. Each node is a "page" holding many sorted keys (often hundreds) plus pointers to child pages. The branching factor — how many children each node has — is called **fan-out**, and it is deliberately huge. High fan-out means the tree stays shallow: a tree only 3 or 4 levels deep can index hundreds of millions of rows. Hundreds of millions of rows, three or four disk reads. That is the whole magic.

Two properties matter and both fall out of the structure:

- **Sorted.** A range query like `WHERE created_at BETWEEN '2026-01-01' AND '2026-02-01'` finds the start key, then walks sideways along the leaves. No re-traversal.
- **Balanced.** Every leaf is the same distance from the root. Lookups have predictable cost whether the key is the smallest, the largest, or in the middle.

## What it is NOT

- Not a hash index. Hash indexes are faster for raw equality but cannot do range scans, `ORDER BY`, or prefix matches.
- Not a bitmap index. Bitmap indexes shine on low-cardinality columns (gender, status) in analytic warehouses, not on OLTP point lookups.
- Not a full-text index. B-trees match whole keys or left-anchored prefixes, not "documents containing the word `bicycle`."
- Not the table itself. The index stores keys and row pointers; the row data lives elsewhere (with one common exception, the clustered index, covered in the deep dive).
- Not literally a "B-tree" in most engines — it is almost always a **B+tree**, a close cousin. The distinction is in the deep dive.

## When you would reach for it

- Looking up rows by a primary key or foreign key.
- Filtering on a column with high selectivity (`email`, `order_id`, `created_at`).
- Range queries: `BETWEEN`, `<`, `>`, `ORDER BY` on the indexed column.
- Left-anchored `LIKE 'foo%'` searches.
- Enforcing uniqueness — a `UNIQUE` constraint is backed by a B-tree.

## When you would NOT reach for it

- Columns with very few distinct values (`is_active` boolean) — the index barely narrows anything.
- Tables small enough that a full scan is already a few milliseconds.
- Write-heavy hot columns where index maintenance cost outweighs read gains.
- Substring search (`LIKE '%foo%'`) — the leading wildcard defeats the sort order.
- Geospatial or vector similarity — those need GiST, R-tree, or specialized ANN indexes.

## Key vocabulary

- **Node / page** — one unit of the tree, sized to a disk block (often 8KB or 16KB).
- **Root, internal, leaf** — top, middle, bottom layers of the tree.
- **Fan-out** — how many children a node can have. High fan-out keeps the tree shallow.
- **Key** — the indexed value (e.g. an `email` string).
- **Pointer / row ID** — what a leaf entry points to; the physical address of the row.
- **Selectivity** — fraction of rows a query keeps. Higher is better for index use.
- **Clustered vs. secondary index** — whether the table is physically stored in index order, or the index sits alongside it.
- **Balanced** — every leaf is at the same depth; the "B" stands for *balanced*, not *binary*.

## What's next

The next document, `02-deep-dive.md`, answers What / Where / When / How / Why in detail — including how nodes split on insert, why B+trees beat plain B-trees for disk storage, and how the query planner actually decides to use your index.
