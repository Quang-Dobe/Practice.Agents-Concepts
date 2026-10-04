# Vector Search — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
Given a set X of N vectors in ℝ^d, a dissimilarity function δ, a query vector q, and an integer k, **k-nearest-neighbour (k-NN) search** returns the k points of X with the smallest δ(q, x). A flat scan solves it exactly in O(N·d) per query. **Approximate nearest-neighbour (ANN) search** returns a set S of size k and is judged by **recall@k** = |S ∩ true top-k| / k under a latency and memory budget. Graph and partition indexes give no worst-case recall guarantee. Only LSH offers a probabilistic (1+ε)-approximation bound, and it pays for that bound in memory.

The three common choices for δ are related. On unit-normalized vectors, ‖q − x‖² = 2 − 2·(q·x), so L2, cosine, and inner product produce the same ranking. On unnormalized vectors, inner product is not a metric because it breaks the triangle inequality. Ranking by it is called **maximum inner product search (MIPS)**, and some indexes need a transform or special handling to support it.

In this document, "vector search" means an ANN index plus the query execution around it: metric, filters, top-k merge, and optional re-ranking.

### The core building blocks
- **Vectors and a metric.** Dense float vectors, usually 384–3072 dimensions, produced by an embedding model. The metric must match the one the model was trained with.
- **Partition-based indexes (IVF family).** k-means splits the space into `nlist` cells, and a query scans only the `nprobe` nearest cells. SPANN and SPFresh are disk- and update-oriented variants of the same idea.
- **Graph-based indexes.** Each vector is a node linked to nearby nodes, and a query walks the graph greedily. Examples are HNSW ([Malkov & Yashunin](https://arxiv.org/abs/1603.09320)), Vamana/DiskANN, and NVIDIA's GPU-built CAGRA.
- **Hash- and tree-based indexes.** LSH and random-projection trees such as Spotify's Annoy. They have largely been displaced by graphs at high recall.
- **Quantization.** This compresses the stored vectors. Scalar quantization to int8 is 4× smaller than float32. Product quantization (PQ) is typically 16–64× smaller. Binary quantization (1 bit per dimension, such as RaBitQ or Elastic's BBQ) is 32× smaller.
- **Re-ranking (refinement).** The index over-fetches candidates using compressed vectors, then re-scores them with full-precision vectors to recover recall.
- **Filter integration.** Pre-filter, post-filter, or filter-aware traversal. This decides whether `WHERE tenant_id = 42` still returns k rows.
- **Query-time knobs.** `ef_search` for graphs and `nprobe`/`probes` for IVF. These move each query along the recall/latency curve.
- **Evaluation setup.** Brute-force ground truth on a query sample plus recall@k. [ANN-Benchmarks](https://ann-benchmarks.com/) is the public reference.

### How it relates to the broader landscape
Vector search is the dense, high-dimensional branch of **similarity search**. Its closest sibling is **lexical retrieval**: BM25 over an inverted index, which matches exact terms in sparse, very high-dimensional space. **Learned sparse retrieval** (SPLADE) sits in between, with model-produced weights stored in an inverted index. **Spatial indexes** such as R-trees and kd-trees solve the same nearest-neighbour problem well in 2–10 dimensions (geo, CAD) and collapse beyond that.

## Where

### Where it runs / lives in the stack
It runs in the **retrieval layer of the data tier**, on the hot path of every semantic query. The application embeds the query by calling a model, sends the vector to the index, and gets back IDs, scores, and payloads. Indexes come in four deployment shapes:
- **In-process library** (FAISS, hnswlib, USearch). Your service owns the index's memory, persistence, and replication.
- **Feature of a general-purpose database** (pgvector, Lucene-based Elasticsearch/OpenSearch, MongoDB Atlas, SQL Server 2025). Vectors live next to the relational or document data.
- **Dedicated vector database** (Milvus, Qdrant, Weaviate, Pinecone). Sharding, filtering, and hybrid search are built in.
- **Object-storage-native engines** (turbopuffer, LanceDB, Amazon S3 Vectors). The data lives in S3-class storage, with an NVMe/RAM cache in front.

### Where you typically encounter it
- The retrieval step of RAG systems and coding assistants that search a codebase or document corpus.
- Candidate generation in recommenders. A two-tower model embeds users and items, and ANN fetches the top few hundred items before a heavier ranker scores them.
- E-commerce "similar products" and visual search.
- Near-duplicate detection for images, support tickets, or scraped pages.
- Hybrid site search in Elasticsearch/OpenSearch, where kNN results are fused with BM25.

### Ecosystem and tooling
- **Index libraries:** FAISS (Meta; flat, IVF, PQ, HNSW, GPU), hnswlib, ScaNN (Google), DiskANN (Microsoft), USearch.
- **In an existing database:** pgvector (current release 0.8.7), Elasticsearch `dense_vector`, OpenSearch k-NN, MongoDB Atlas Vector Search, SQL Server 2025 `VECTOR` type. SQL Server's DiskANN index is still behind `PREVIEW_FEATURES` ([docs](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-vector-index-transact-sql)).
- **Dedicated engines:** Milvus, Qdrant, Weaviate, Pinecone, Vespa.
- **Object-storage and serverless:** turbopuffer, LanceDB, S3 Vectors.
- **GPU acceleration:** NVIDIA cuVS (CAGRA, IVF-PQ), integrated into FAISS from 1.10.0.
- **Hybrid fusion:** Reciprocal Rank Fusion (RRF) is built into Elasticsearch, OpenSearch, Weaviate, and Azure AI Search.
- **Benchmarks:** ANN-Benchmarks and big-ann-benchmarks.

## When

### When the topic emerged and why
- **1975–1998: trees hit the dimensionality wall.** kd-trees answer nearest-neighbour queries well in low dimensions. [Weber, Schek & Blott (VLDB 1998)](https://www.vldb.org/conf/1998/p194.pdf) showed that tree and partitioning methods lose to a sequential scan above roughly 10 dimensions. That result pushed the field toward approximation.
- **1998: LSH.** Indyk & Motwani introduced locality-sensitive hashing, the first sublinear approximate scheme with guarantees. High recall needed many hash tables, so memory grew quickly.
- **2011: product quantization.** Jégou, Douze & Schmid (TPAMI) compressed vectors into a few bytes each and computed distances through lookup tables. IVFADC (IVF + PQ) made billion-scale search fit in RAM.
- **2014–2016: navigable small-world graphs.** NSW, then HNSW (arXiv 2016, TPAMI 2018), gave logarithmic-scaling search with high recall. HNSW became the default in-memory index.
- **2017–2020: industrial libraries.** FAISS was open-sourced in 2017. [DiskANN (NeurIPS 2019)](https://papers.nips.cc/paper/2019/hash/09853c7fb1d3f8ee67a61b6bf4a7f8e6-Abstract.html) served 1B SIFT vectors from a 64 GB RAM machine plus an SSD at >5,000 QPS, <3 ms mean latency, and 95%+ 1-recall@1. ScaNN followed in 2020.
- **2023 onward: the LLM wave.** RAG turned ANN from a recommender and IR specialty into general application infrastructure. pgvector added HNSW in 0.5.0 (August 2023).
### When to use it in a project
Reach for it when:
- Queries describe intent in natural language or by example ("like this image"), and exact-term matching misses relevant results.
- The corpus is large enough that a flat scan breaks your latency budget. As a rough guide, a flat scan of 1M × 768-dim float32 vectors touches about 3 GB per query.
- A recall of about 0.95 is acceptable, because a downstream step (an LLM, a reranker, or a human) tolerates a few misses.
- You already embed content for other reasons, so the marginal cost of an index is small.

### When NOT to use it
Avoid it when:
- The corpus has fewer than about 10⁴–10⁵ vectors. A flat scan is exact, simple, and fast enough. An index adds build cost and tuning for no gain.
- Lookups are exact: IDs, SKUs, error codes, emails. Use B-tree or inverted indexes.
- Recall must be complete and auditable, as in e-discovery or compliance holds.
- Structured predicates already narrow the set to a few thousand rows. Filter first, then rank exactly.
- The team cannot run an embedding pipeline. Re-embedding on every model change is a real operational cost.

## How

### How it works under the hood

**HNSW build and query**

```
Layer 2:  E ───────────────────────── X           few nodes, long-range links
          │
Layer 1:  E ───── B ───────── X ───── Y           more nodes, medium links
                  │
Layer 0:  E ─ C ─ B ─ D ─ F ─ X ─ G ─ Y ─ (q)     every node, up to 2·M links
```

1. **Level assignment.** Each inserted vector draws a top layer from an exponentially decaying distribution with normalization factor m_L = 1/ln(M). Most nodes exist only on layer 0, which makes the structure a probabilistic skip list over a proximity graph.
2. **Insert.** Starting from the global entry point, greedily descend the layers above the node's top layer. On each layer from there down, run a beam search of width `ef_construction`. Then pick up to M neighbours (2·M on layer 0) with a diversity heuristic: a candidate is dropped if it is closer to an already-chosen neighbour than to the new node. This keeps long-range edges and prevents isolated clusters.
3. **Query.** Do a greedy descent with beam width 1 through the upper layers. On layer 0, run a best-first search that keeps the `ef_search` best candidates. Stop when the nearest unexplored candidate is farther than the worst kept result. Return the top k.
4. **Cost.** Search cost grows roughly as log N times the node degree. Memory is the raw vectors plus about 2·M × 4 bytes of neighbour IDs per node. For 1M × 1536-dim float32 vectors, that is about 6.1 GB of vectors versus about 0.13 GB of graph at M=16. The vectors dominate memory, which is why quantization matters more than M.

**IVF (and IVF-PQ) build and query**
1. **Train** k-means on a representative sample to get `nlist` centroids. pgvector recommends `lists = rows/1000` up to 1M rows and `sqrt(rows)` beyond.
2. **Assign** each vector to its nearest centroid's inverted list.
3. **Optional PQ.** Split each residual vector into m sub-vectors and quantize each one to one of 256 sub-centroids, so each vector costs m bytes. For example, 1536 dimensions with m=192 gives 192 bytes, versus 6,144 bytes as float32.
4. **Query.** Compute distances to all centroids and pick the `nprobe` nearest. For PQ, precompute an m × 256 distance lookup table per query (asymmetric distance computation). Scan the chosen lists using table lookups, keep a top-k heap, and optionally re-rank with the full vectors.

**Defaults you will actually hit** (verified):

| Engine | Build params | Query param |
|---|---|---|
| pgvector HNSW | `m=16`, `ef_construction=64` | `hnsw.ef_search=40` |
| pgvector IVFFlat | `lists` (no safe default; size it) | `ivfflat.probes=1` |
| FAISS `IndexHNSW` | M set by caller (32 is common), `efConstruction=40` | `efSearch=16` |
| Elasticsearch 9.1+ | `bbq_hnsw` default for >384 dims | `num_candidates` |

**Filtering strategies**
- **Post-filter.** Run ANN, then drop non-matching rows. With a selective filter, this returns fewer than k rows.
- **Pre-filter.** Compute the matching set first, then scan it exactly. This is ideal when the filter is selective, and the cost grows with the size of the matching set.
- **Filter-aware traversal.** The graph walk skips or bridges non-matching nodes. Qdrant adds per-payload-value edges ("filterable HNSW") and a cardinality-based query planner. [ACORN (SIGMOD 2024)](https://arxiv.org/abs/2403.04871) expands neighbour lists so predicate subgraphs stay navigable. Weaviate added it in v1.27 and made it the default in v1.34.
- **Iterative scan.** pgvector 0.8.0 (October 2024) keeps fetching from the index until k rows pass the filter, capped by `hnsw.max_scan_tuples` (default 20,000).

### Key trade-offs

| Design choice | You gain | You give up |
|---|---|---|
| HNSW over IVF | High recall at low latency; incremental inserts; no training step | Slow, memory-hungry build; random-access pattern needs RAM; deletes degrade the graph |
| IVF over HNSW | Fast build; small overhead; partitions map cleanly to disk and object storage | Needs representative training data; recall depends heavily on `nprobe`; drifts as data changes |
| Quantization (int8 / PQ / binary) | 4–32× less memory and bandwidth | Recall loss, which you claw back with over-fetch plus full-precision re-rank |
| Higher `M` / `ef_construction` | Better graph, higher recall ceiling | Longer builds, larger index |
| Higher `ef_search` / `nprobe` | Recall, per query | Latency, roughly linear in the knob |
| RAM vs SSD (DiskANN) vs object storage | Latency of a few ms in RAM | 5–10× more vectors per node on SSD; ~100 ms+ per round trip on object storage, much cheaper |
| Dedicated engine vs pgvector | Sharding, filter-aware ANN, hybrid search out of the box | A second datastore to keep consistent with the source of truth |

### Common failure modes
- **A filtered query returns 3 rows instead of 10.** Cause: post-filtering an `ef_search=40` candidate list. A 10%-selective filter leaves about 4 matches.
- **Recall quietly drops weeks after launch.** Cause: update/delete churn leaves tombstones and [unreachable nodes](https://arxiv.org/abs/2407.07871) in the graph, so the index needs periodic rebuilds.
- **IVF recall falls after a data shift.** Cause: centroids were trained on old or tiny data, so the lists become unbalanced. pgvector warns to build IVFFlat only after the table has data.
- **The Postgres index is never used.** Cause: the query operator doesn't match the opclass (for example `<->` against `vector_cosine_ops`), or the planner costs a high `ef_search` above a sequential scan.
- **p99 jumps from milliseconds to hundreds of milliseconds.** Cause: the HNSW index outgrew RAM and every hop became a page fault.
- **Scores look reasonable but results are garbage.** Cause: vectors from two embedding models, or two model versions, were mixed in one index. Their spaces are not comparable.
- **Ranking is subtly wrong.** Cause: the metric doesn't match the model's training (L2 on unnormalized vectors from a dot-product model).
- **Searches for "ERR_4012" or a SKU miss.** Cause: dense embeddings blur rare exact tokens. Add BM25 and fuse the results with RRF (k=60 by convention, per Cormack et al. 2009).
- **A fixed similarity threshold behaves inconsistently.** Cause: cosine scores are not calibrated across queries or models.

## Why

### Why it exists
Exact nearest-neighbour search in high dimensions has no known algorithm that is both sublinear and free of exponential dependence on d. At N = 10⁸ and d = 1024, a flat scan reads hundreds of gigabytes per query. That is a memory-bandwidth problem no CPU solves at interactive QPS. ANN trades a measured, tunable amount of correctness (recall) for orders of magnitude less data touched per query. It exploits the fact that real embeddings have a much lower *intrinsic* dimension than their nominal d, so locality survives in a navigable structure even when trees fail.

### Why it looks the way it does
**Why graphs beat LSH and trees.** LSH gives guarantees but needs many hash tables for high recall, which multiplies memory. Trees degrade toward a scan above about 10 dimensions. Graphs adapt to the data's intrinsic dimension and reach 0.95+ recall while visiting a tiny fraction of nodes.

**Why HNSW has layers instead of a flat graph.** A flat NSW graph mixes long and short links. Searches waste hops, and complexity scales polylogarithmically. HNSW separates links by length scale, like a skip list, so the coarse layers cover distance quickly and layer 0 refines. That gives log-scaling hop counts.

**Why DiskANN goes back to one flat layer.** On SSD, each hop is a random 4 KB read, so the hop count matters more than CPU. Vamana's α-pruning deliberately keeps long edges in a single graph to cut hops. Compressed PQ codes stay in RAM to steer the search, and full vectors are fetched from disk only for re-ranking.

**Why object-storage engines use IVF-style partitions.** A graph walk on S3 would take one ~100 ms round trip per hop. Centroid indexes need about 3–4 round trips total, and a partition is a natural unit for batch writes. [turbopuffer](https://turbopuffer.com/docs/architecture) reports about 400–500 ms cold and about 16 ms p50 warm.

The general rule: **index structure follows the cost of a random access on the storage medium.** Graphs suit RAM, flat long-edge graphs suit SSD, and partitions suit object storage.

### Why it matters now
As of 2026, vector search is consolidating into a feature of existing databases rather than a product category of its own:
- pgvector is the default answer in Postgres shops.
- SQL Server 2025 ships a `VECTOR` type.
- Elasticsearch 9.1+ quantizes by default with `bbq_hnsw`.
- [Amazon S3 Vectors](https://aws.amazon.com/about-aws/whats-new/2025/12/amazon-s3-vectors-generally-available/) went GA on 2 December 2025, with up to 2 billion vectors per index, sub-second infrequent queries, and about 100 ms for frequent ones.

The open engineering problems have moved from "which index" to four narrower areas:
- Filtered recall (ACORN-style traversal).
- Aggressive quantization with re-ranking (RaBitQ, BBQ).
- Tiered RAM/SSD/object storage for cost.
- GPU index builds. cuVS CAGRA builds up to about 12× faster than CPU HNSW in Meta's FAISS benchmarks.

The core is stable. Storage tiering and filtering are where it is still changing.

## Open questions / things to verify in practice
- At pgvector defaults (`ef_search=40`), what is recall@10 on my own data against a brute-force ground truth? What `ef_search` reaches 0.95, and what does that cost in p95 latency?
- How does filtered recall change at 50%, 5%, and 0.5% filter selectivity, and how much does `hnsw.iterative_scan = relaxed_order` recover?
- What is the recall difference between `vector`, `halfvec`, and binary quantization with full-precision re-ranking, and how does it compare to the memory saved?
- After deleting or updating 30% of rows, how far does recall drop, and does `REINDEX` fully restore it?
- Do exact-token queries (codes, SKUs) measurably improve with BM25 + RRF hybrid search compared with pure vector search?