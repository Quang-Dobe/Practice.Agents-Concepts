# Vector Search — Overview

> Vector search finds the stored vectors closest to a query vector, which turns "find things similar to this" into a geometry problem. In practice it means approximate nearest-neighbour search over embeddings.

## The 30-second version
Once text, images, or products are turned into embeddings (lists of numbers), "similar" means "close together in space." Vector search answers one question: given a query vector, which K stored vectors are nearest? Checking every vector gives the exact answer but gets too slow at millions of items. So almost every real system uses an approximate index. These indexes return about 95–99% of the true top-K in a few milliseconds instead of a full scan. This is the retrieval step behind semantic search, recommendations, deduplication, and RAG.

## The mental model
Picture a city map with ten million coffee shops pinned on it. You are standing at one spot and want the 10 closest shops.

**The exact way:** measure your distance to all ten million pins and take the top 10. This always gives the right answer, and it is far too slow at a thousand queries per second.

**The approximate way:** organize the map in advance so you only look at a small part of it. There are two common ways to do that.

- **Districts (IVF).** Split the city into 1,000 districts, each with a central landmark. To search, find the few landmarks nearest to you and check only the shops in those districts. If the right shop sits just over the border of a district you skipped, you miss it. Checking more districts means fewer misses but a slower search.
- **Highways and side streets (HNSW).** Build a road network. A sparse top level has a few long highways. Lower levels add more and more local streets. You start on a highway, get off near your area, and walk the side streets toward closer and closer shops. It is fast and usually right, but you can sometimes end up on a street that only *looks* like the closest one.

So vector search always balances three things: **recall** (did you find the true nearest neighbours), **latency**, and **memory**. Every index setting moves you somewhere between those three.

One more piece: "distance" can be measured in different ways. **Cosine similarity** compares direction. **Dot product** compares direction and length. **Euclidean (L2)** measures straight-line distance. If your vectors are normalized to length 1, all three give the same ranking. Use the metric your embedding model was trained for.

## What it is NOT
- Not embeddings. Embeddings are the coordinates. Vector search is how you look things up among them.
- Not a vector database. Pinecone, Qdrant, Milvus, and `pgvector` are *products* that store vectors and run vector search, plus filtering, persistence, and replication.
- Not keyword search. BM25 and full-text indexes match exact terms. Vector search matches by meaning and can miss an exact SKU or error code.
- Not RAG. RAG is a pattern that *uses* vector search to fetch context for an LLM.

## When you would reach for it
- Semantic search where users describe what they want instead of typing the exact title.
- The retrieval step of a RAG pipeline over thousands to billions of chunks.
- "More like this" recommendations for products, articles, or images.
- Finding near-duplicate content, such as reposted images or reworded support tickets.
- Hybrid search, where vector results are merged with keyword results to get both meaning and exact matches.

## When you would NOT reach for it
- You need exact lookups by ID, email, or SKU. Use a B-tree index.
- The dataset is small (roughly tens of thousands of vectors or fewer). A brute-force scan is exact and fast enough, so you can skip the index.
- Results must be fully explainable or guaranteed complete, as in legal discovery or compliance. Approximate recall is a liability there.
- Hard structured filters do most of the work, like "orders from tenant 42 last week." Filter first and rank second, or vector search becomes the wrong tool.

## Key vocabulary (just enough to keep reading)
- **k-NN**: exact K-nearest-neighbour search. It compares the query against every vector.
- **ANN**: approximate nearest neighbour. It trades a little accuracy for a large speedup.
- **Recall@K**: the share of the true top-K that the index actually returned.
- **Similarity metric**: cosine, dot product, or L2. This defines what "close" means.
- **HNSW**: a layered graph index. It is fast with high recall but uses a lot of RAM. See the [original paper](https://arxiv.org/abs/1603.09320).
- **IVF**: a cluster-based index that searches only the `nprobe` nearest clusters.
- **Product quantization (PQ)**: compresses vectors so more fit in memory, at some cost in accuracy.

## What's next
The next document answers What / Where / When / How / Why in detail. It covers how HNSW and IVF are built and queried, which settings control the recall/latency trade-off, how filtering interacts with ANN, and when disk-based indexes like DiskANN make sense.
