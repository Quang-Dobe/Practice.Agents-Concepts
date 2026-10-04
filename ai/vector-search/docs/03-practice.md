# Vector Search — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic
In a typical RAG backend, vector search is a `vector(1536)` column on a `chunks` table in Postgres. An HNSW index sits on it, and one `ORDER BY embedding <=> $1 LIMIT 20` query runs on every chat turn. Nobody owns it until recall drops or the index outgrows RAM.

In multi-tenant SaaS ("search my workspace", "ask my docs"), it is a per-tenant retrieval layer. The tenant filter matters as much as the ANN index. Most incidents here are filtered-recall bugs or cross-tenant leaks, not algorithm problems.

In recommenders and e-commerce, it is the candidate-generation stage. ANN pulls a few hundred items in under 10 ms, and a heavier ranker decides what the user actually sees. Recall only has to be good enough for the ranker to work with.

## Best practices

### 1. Build a recall harness before you tune anything
**Do:** Sample 500–1,000 real production queries. Compute exact top-k with a flat scan (FAISS `IndexFlat`, or pgvector with `SET enable_indexscan = off`). Then report recall@k at several `ef_search`/`nprobe` values on a nightly schedule.
**Why:** ANN never throws errors. Defaults like pgvector `hnsw.ef_search=40` or `ivfflat.probes=1` return *something* fast, and recall decay from churn or data drift stays invisible until users complain.
**Avoid:** Checking five demo queries by eye and calling it tuned.

### 2. Version embeddings like a schema
**Do:** Store `embedding_model` and version next to every vector. A new model gets a new column, index, or namespace. Backfill it, shadow-evaluate it with the recall harness and a labelled query set, then switch over behind a flag.
**Why:** Vectors from two models live in incompatible spaces. A mixed index returns plausible-looking scores and nonsense results. Providers also ship new model versions, so a pinned version is your only defence.
**Avoid:** Re-embedding in place inside the live index ("it'll be consistent by morning").

### 3. Size for RAM, and quantize before you shard
**Do:** Budget N × d × bytes-per-dim, plus graph overhead. In pgvector, count it twice, because the HNSW index stores its own copy of each vector next to the heap. Reach for `halfvec` (2× smaller) or binary quantization with full-precision re-ranking on a 4–10× over-fetch before you add nodes.
**Why:** HNSW access is random. Once the index stops fitting in memory, every hop becomes a page fault and p99 jumps from single-digit milliseconds to hundreds.
**Avoid:** Adding replicas to fix latency when the real fix was halving the footprint.

### 4. Choose a filter strategy per filter, deliberately
**Do:** Know how selective each filter is. Tenant IDs get physical isolation: pgvector `PARTITION BY LIST`, a partial index, or Qdrant's `is_tenant` payload index. Mid-selectivity filters get filter-aware traversal or pgvector 0.8+ `hnsw.iterative_scan = relaxed_order`. Very selective filters (a few thousand rows) get an exact scan.
**Why:** If you post-filter a 40-candidate list with a 10% filter, you get back about 4 rows when you asked for 10. The query succeeds, and the answer is quietly worse.
**Avoid:** Adding `WHERE` to an ANN query and trusting the planner.

### 5. Use hybrid retrieval for anything users type into
**Do:** Run BM25 and vector retrieval in parallel, fuse them with Reciprocal Rank Fusion (k=60), and run a cross-encoder reranker over the top 50–100. Anthropic measured top-20 retrieval failure falling 35% with contextual embeddings alone, 49% with BM25 added, and 67% with reranking on top ([source](https://www.anthropic.com/news/contextual-retrieval)).
**Why:** Dense embeddings blur rare tokens. Error codes, SKUs, function names, and people's names get missed.
**Avoid:** Pure dense retrieval for support search or code search.

### 6. Build indexes in bulk, with memory raised
**Do:** Load the data first. Then, for that session only, raise `maintenance_work_mem` (pgvector) and `max_parallel_maintenance_workers`, and run `CREATE INDEX CONCURRENTLY`. Watch the build log for `NOTICE: hnsw graph no longer fits into maintenance_work_mem`.
**Why:** At the Postgres default of 64 MB, an HNSW build over millions of rows spills to disk and runs many times slower. An IVFFlat index built on an empty table trains centroids on nothing.
**Avoid:** Running `CREATE INDEX` without `CONCURRENTLY` on a live table, which blocks writes for the entire build.

### 7. Don't re-embed what didn't change
**Do:** Hash each chunk's text and its metadata separately. A text change triggers a re-embed. A metadata-only change gets a cheap payload patch.
**Why:** The embedding pipeline usually costs more than the queries. Notion cut data volume 70% with xxHash-based change detection ([source](https://www.notion.com/blog/two-years-of-vector-search-at-notion)).
**Avoid:** Re-embedding the whole document on every save event.

### 8. Schedule rebuilds against churn
**Do:** Track the fraction of rows updated or deleted since the last build. Rebuild (`REINDEX INDEX CONCURRENTLY`) when the recall harness shows drift. Commonly that happens somewhere around 20–30% churn, but measure it on your own data. In Elasticsearch/OpenSearch, keep segment counts low, because each segment is its own HNSW graph and every query searches all of them ([Elastic tuning guide](https://www.elastic.co/docs/solutions/search/vector/knn/optimize-performance-accuracy)).
**Why:** Tombstones and unreachable nodes erode graph recall weeks after launch.
**Avoid:** Assuming `VACUUM` repairs the graph. In pgvector it is also slow on HNSW, and the docs recommend reindexing first.

### 9. Never hard-code a similarity cutoff
**Do:** Return top-k and let a reranker or the LLM judge relevance. If you need a "no good match" state, calibrate the threshold per model on labelled pairs, and recalibrate on every model change.
**Why:** Cosine scores are not calibrated across queries or models. `similarity > 0.8` means "most results" for one model and "nothing" for the next.
**Avoid:** Shipping a magic number copied from a blog post.

### 10. Assert index usage in CI
**Do:** Keep `EXPLAIN (ANALYZE, BUFFERS)` checks for hot queries. Confirm the operator matches the opclass (`<=>` with `vector_cosine_ops`) and that the plan shows an index scan.
**Why:** An operator mismatch, an implicit cast, or a planner cost flip turns a 5 ms query into a sequential scan with no error.
**Avoid:** Finding out from a p99 alert after a "harmless" query refactor.

## Anti-patterns to recognize

- **Vector store as source of truth**: Chunks and metadata live only in the vector DB. When you change the model or the engine, you can't rebuild because the original text is gone or stale. Keep canonical content in your primary store and treat the index as a derived, rebuildable projection.
- **Post-retrieval tenant filtering**: The app fetches the global top-50 and then drops other tenants' rows in code. It returns empty results for small tenants, and one missed code path leaks another customer's data. Enforce the tenant scope inside the query (partition, payload filter, or RLS) on every call.
- **Collection-per-tenant at unbounded scale**: One index per customer looks clean at 50 tenants and collapses at 50,000 under per-collection overhead. Qdrant explicitly advises one collection with payload partitioning, and dedicated shards only for a few large tenants. Object-storage engines like turbopuffer are the exception, because their namespaces are designed to be cheap.
- **Benchmark-driven engine choice**: Picking an engine from ANN-Benchmarks charts, which measure unfiltered, static, single-node data. Your workload has filters, updates, and multi-tenancy. Benchmark your own queries with your filters and update rate.
- **Cranking `ef_search` globally**: Setting `ef_search=1000` because recall was bad on one query class. Latency grows roughly linearly, and the planner may start preferring sequential scans. Set it per query or per transaction (`SET LOCAL`), and fix filtered-recall problems with a filter strategy, not brute force.
- **One vector per huge document**: Embedding a 40-page PDF as one vector averages away every specific fact. Chunk on semantic boundaries (sections, functions) and store a back-reference to the parent document.
- **A dedicated vector DB on day one**: Adding a new datastore, a sync pipeline, and dual-write consistency issues for 200k vectors. Two camps exist here. The Postgres-first camp argues that transactions, RLS, and joins outweigh everything until about 10M vectors. The dedicated-engine camp points to filter-aware ANN, hybrid search, and sharding out of the box. The usual tie-breaker is whether your vectors already sit next to relational data that needs to stay consistent with them.

## Real-world usage patterns

**Workspace AI search at billions of objects.** Notion partitions vectors by workspace for its AI Q&A. It grew vector capacity 8× in about five months, then moved to turbopuffer, an object-storage-native engine. That cut search-engine spend 60% and improved p50 latency from 70–100 ms to 50–70 ms. *Lesson:* past launch, the expensive part was the embedding pipeline, not the queries. Change detection and self-hosted embeddings delivered the largest savings.

**Codebase retrieval for a coding assistant.** Cursor stores one namespace per codebase, more than 80 million namespaces in total ([turbopuffer case study](https://turbopuffer.com/customers/cursor)). Files are chunked by syntax tree, embedded, and queried per question. *Lesson:* most namespaces are cold at any given moment. Storage tiering (active namespaces in NVMe/RAM, the long tail in S3) matters more than index choice, and copying vectors between forks of the same repo avoids paying to re-embed them.

**Candidate generation for music recommendations.** Spotify used ANN to feed rankers for features like Discover Weekly. It replaced its own tree-based Annoy with Voyager, which is HNSW-based: about 10× faster at similar recall and up to 4× less memory ([Spotify Engineering](https://engineering.atspotify.com/introducing-voyager-spotifys-new-nearest-neighbor-search-library)). *Lesson:* when hundreds of services embed an in-process library, its on-disk format and API stability become as important as raw speed. That is why Spotify built its own library instead of using hnswlib as-is.

**Postgres-first RAG for a B2B product.** Chunks sit in the same database as accounts, with RLS enforcing the tenant scope and pgvector HNSW for ranking. Deletes are transactional, so a removed document disappears from search right away. *Lesson:* the pain arrives at index rebuild time, around tens of millions of rows. Builds need gigabytes of `maintenance_work_mem` and hours of time, so plan a partitioned layout before you need it.

## Operational checklist

- [ ] Does a nightly job report recall@10 against brute-force ground truth, with an alert below the agreed floor (for example 0.95)?
- [ ] Are p50/p95/p99 query latency, index size vs available RAM, and cache hit ratio on dashboards?
- [ ] Does every vector record its embedding model and version, and is there a written re-embedding runbook?
- [ ] Is the tenant or ACL filter enforced inside the vector query on every path, with a test proving that tenant A cannot retrieve tenant B's chunks?
- [ ] For each production filter, has filtered recall been measured at realistic selectivity, and does the query return k rows?
- [ ] What happens when the embedding API is down or rate-limited: cached query embeddings, a BM25-only fallback, or a hard failure? Has that path been tested?
- [ ] Is the embedding spend bounded by change detection, and is there a cost alert on embedding tokens per day?
- [ ] Is there a churn-triggered rebuild procedure (`REINDEX CONCURRENTLY` or a blue/green index swap) that has been rehearsed on a production-sized copy?
- [ ] Do hot queries have an `EXPLAIN` assertion or a plan-regression check?
- [ ] Does a new engineer know where the source text lives, which model is live, and that the index can always be rebuilt from scratch?

## How this topic typically evolves in a codebase
Teams start with a flat scan or a default HNSW index on a pgvector column, one embedding model, no filters, and no recall measurement. That is the right call for a prototype: a few hundred thousand vectors, sub-10 ms queries, and no new infrastructure.

The first pain arrives with tenants and filters. Filtered queries return short result sets, someone bolts on post-filtering, and keyword-shaped queries start failing. That forces hybrid search, a filter strategy, and the first recall harness. The second pain is the first embedding-model upgrade. Teams that didn't version vectors discover they need a full parallel index and a backfill, which costs days of embedding spend and a careful cutover.

The painful migration point is usually around tens of millions of vectors, or when per-tenant isolation dominates. The index stops fitting in RAM, rebuilds take hours, and a lot of embedding spend goes to unchanged content. Teams then either quantize hard and partition inside Postgres, or move to a dedicated or object-storage engine and keep the primary database as the source of truth. Teams that treated the index as a derived, rebuildable projection from day one make this move in weeks. Teams that didn't spend a quarter on it.

## Further reading
- [pgvector README](https://github.com/pgvector/pgvector): the authoritative source for index options, iterative scans, quantization recipes, and build-memory tuning.
- [Two years of vector search at Notion](https://www.notion.com/blog/two-years-of-vector-search-at-notion): a rare end-to-end cost and scaling story, covering sharding, an engine migration, and change detection.
- [Elastic: optimize approximate kNN performance](https://www.elastic.co/docs/solutions/search/vector/knn/optimize-performance-accuracy): explains how segment count, the filesystem cache, and quantization drive latency in Lucene-based engines.
- [Qdrant multitenancy guide](https://qdrant.tech/documentation/guides/multiple-partitions/): concrete guidance on payload partitioning vs collections vs custom shards.
- [Anthropic: Contextual Retrieval](https://www.anthropic.com/news/contextual-retrieval): measured gains from hybrid BM25 + embeddings + reranking on real RAG workloads.
- [Introducing Voyager (Spotify)](https://engineering.atspotify.com/introducing-voyager-spotifys-new-nearest-neighbor-search-library): why a large team replaced a tree index with HNSW and built its own library to do it.
