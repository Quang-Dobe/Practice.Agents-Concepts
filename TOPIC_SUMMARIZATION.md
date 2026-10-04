# Vector Search

Vector search finds the stored vectors closest to a query vector. Once text, images, or products are turned into embeddings, which are lists of numbers, "similar" simply means "close together in space," so finding similar things becomes a geometry problem: given a query, which K stored vectors are nearest?

It is the retrieval step behind semantic search, "more like this" recommendations, near-duplicate detection, and RAG pipelines. Checking every vector gives the exact answer but gets too slow at millions of items, so real systems use an approximate index that returns about 95 to 99 percent of the true top results in a few milliseconds. Every setting trades between recall, latency, and memory. You skip it for exact lookups by ID or SKU, for small datasets where a brute-force scan is fast enough, and when results must be guaranteed complete.

Picture a city map with ten million coffee shops and you want the ten closest. Measuring the distance to every pin is exact but far too slow. An IVF index splits the city into districts with a landmark each and only checks the districts nearest to you, so a shop just over a skipped border can be missed. An HNSW index builds a road network: you start on sparse highways, exit near your area, and walk side streets toward closer and closer shops. It is fast and usually right, but it uses a lot of memory.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/ai/vector-search/present/index.html
