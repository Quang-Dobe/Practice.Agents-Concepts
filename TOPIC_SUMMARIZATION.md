# B-Tree Index

A B-tree index is a sorted, shallow, very wide tree that a database keeps alongside your table so it can locate rows by key in a handful of disk reads instead of scanning every page. Each node holds hundreds of sorted keys plus pointers to child nodes, so a tree only three or four levels deep can index hundreds of millions of rows. It is the default index type in Postgres, MySQL/InnoDB, SQL Server, Oracle, and SQLite — almost every `CREATE INDEX` you write is silently building one.

Engineers reach for a B-tree whenever a query filters on a high-selectivity column — a primary key, a foreign key, an email, a timestamp range — and the table is large enough that a full scan would be too slow. B-trees stay balanced as data grows, support range scans cheaply because the leaves are kept in sorted order, and back every `UNIQUE` constraint behind the scenes. They are not the right pick for low-cardinality columns, substring searches with a leading wildcard, or vector similarity, but for the bread-and-butter "find these rows by key" workload they are the structure the database planner reaches for first.

The intuition is a phone book of phone books. The first page is not names but signposts — "A–F lives in volume 1, G–M in volume 2." You open volume 2 and its first page is again signposts pointing into smaller sections. After three or four hops you land on a single page of sorted names and scan it. That is the whole trick: very high branching factor, very shallow tree, logarithmic lookups, and effectively free range scans because the bottom pages are sorted.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/database/b-tree-index/
