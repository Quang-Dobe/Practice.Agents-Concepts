# Embeddings — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
An embedding is a learned function `f: X -> R^d` that maps an input `x` (a token, sentence, image patch, audio clip, graph node) to a dense vector in a `d`-dimensional real-valued space, such that a chosen similarity metric `s(f(a), f(b))` correlates with a task-defined notion of relatedness between `a` and `b`. The model is parameterized by a neural network whose weights are fit so that the geometry of the output space encodes the semantics of the input distribution.

Two embeddings produced by different models live in different vector spaces and are not comparable, even at the same dimensionality. The space is defined by the training objective, not by `d`.

### The core building blocks
- **Encoder**: the neural network that produces the vector. For text, almost always a transformer (BERT-family or decoder-only LLM with a pooling head).
- **Pooling head**: turns a sequence of token vectors into one fixed-length vector. Common choices: `[CLS]` token, mean-pool, last-token (for decoder models), attention-weighted pool.
- **Projection / normalization**: a final linear layer plus L2 normalization so vectors live on the unit hypersphere. Most modern text embeddings ship pre-normalized.
- **Vector space**: `R^d`, typically `d in {384, 768, 1024, 1536, 3072}`. Higher `d` gives more capacity, more storage, and more cost per similarity calculation.
- **Similarity metric**: cosine, dot product, or Euclidean (L2) distance. For unit-normalized vectors all three rank in the same order; cosine = dot product, and Euclidean is a monotone transform.
- **Loss / training signal**: contrastive (InfoNCE), triplet, or masked-language-modeling objectives — covered below.

### How it relates to the broader landscape
Embeddings are the dense end of a continuum that also contains sparse representations (one-hot, TF-IDF, BM25) and structured representations (knowledge-graph entities). They are the standard interface between unstructured data and any retrieval, clustering, or downstream-classification step in modern ML. Within the embedding family, the major axes are: static vs contextual, single-vector vs multi-vector (ColBERT), text-only vs multimodal (CLIP, SigLIP, Gemini Embedding 2), and fixed-dim vs Matryoshka-truncatable.

## Where

### Where it runs / lives in the stack
At the **application / ML-inference layer**. Embedding generation is a model call (local GPU/CPU inference or a hosted API like OpenAI, Cohere, Voyage). The resulting vectors live in a **storage layer** — a vector database (Pinecone, Qdrant, Weaviate, Milvus), a relational extension (pgvector, SQL Server vector type), or a search engine plugin (OpenSearch k-NN, Elasticsearch dense_vector). Query-time similarity search runs inside that storage layer; downstream consumers (a RAG pipeline, a classifier, an LLM prompt builder) sit on top.

### Where you typically encounter it
- **RAG pipelines**: chunk -> embed -> store -> retrieve -> stuff into LLM prompt.
- **Semantic search** in product catalogs, support knowledge bases, code search (GitHub Copilot's repo search, Sourcegraph).
- **Recommendation systems**: two-tower / dual-encoder models at YouTube, Spotify, Pinterest.
- **Clustering and deduplication**: customer-feedback grouping, near-duplicate document detection.
- **Anomaly detection**: embed a record, flag the ones far from any cluster centroid.
- **Multimodal retrieval**: CLIP/SigLIP for "find images matching this text caption."

### Ecosystem and tooling
- **For generating embeddings (hosted)**: OpenAI `text-embedding-3-small` / `-large`, Cohere `embed-v4`, Voyage `voyage-3-large`, Google `gemini-embedding-001` (and Gemini Embedding 2 for multimodal, March 2026).
- **For generating embeddings (open weights)**: `bge-*` (BAAI), `nomic-embed-text-v1.5`, `nv-embed-v2` (NVIDIA), `Qwen3-Embedding-8B`, `e5-mistral-7b-instruct`, `sentence-transformers/*`.
- **For storing and searching**: Pinecone, Qdrant, Weaviate, Milvus, Chroma, pgvector, OpenSearch k-NN, FAISS (library, not a service).
- **For benchmarking**: MTEB (Massive Text Embedding Benchmark) on Hugging Face is the de-facto leaderboard; BEIR for retrieval-only.

## When

### When the topic emerged and why
- **2003**: Bengio's neural language model — first dense word vectors as a byproduct of next-word prediction.
- **2013**: word2vec (Mikolov) — skip-gram / CBOW made dense word vectors cheap to train and gave the famous `king - man + woman ~ queen` analogy.
- **2014**: GloVe (Pennington) — matrix factorization of a global co-occurrence matrix instead of local-window prediction.
- **2018**: ELMo, then BERT — contextual embeddings: the vector for "bank" now depends on the surrounding sentence.
- **2019**: Sentence-BERT — bolted a siamese training scheme onto BERT so the `[CLS]`/mean-pool vector became actually useful for sentence-level similarity. Before this, raw BERT embeddings were notoriously bad at STS tasks.
- **2020**: DPR (Dense Passage Retrieval) — the contrastive + in-batch-negatives + hard-negatives recipe that every modern dense retriever still uses.
- **2022-2024**: OpenAI `text-embedding-ada-002`, then `text-embedding-3-*` with Matryoshka Representation Learning.
- **2025-2026**: open-weight models (NV-Embed-v2 at 72.31 MTEB, Qwen3-Embedding) catching and overtaking closed APIs; multimodal embeddings going mainstream.

Each step removed a constraint: from "one vector per word, no context" to "one vector per sentence, full context" to "one vector across modalities, truncatable on demand."

### When to use it in a project
Reach for embeddings when:
- The retrieval signal is **semantic**, not lexical (paraphrases, synonyms, multilingual queries).
- You have **more than a few thousand items** — below that, BM25 plus a coffee break works.
- You need a **shared latent space** across modalities or languages.
- You are doing **clustering, classification by nearest-example, or deduplication** on unstructured data.

### When NOT to use it
Avoid embeddings when:
- Queries are **exact-match or boolean filters** (SKU lookup, date range).
- Your corpus is **dominated by rare entity names** (drug codes, part numbers) — BM25 still wins; hybrid search is the answer.
- You need an **auditable decision** — "the cosine was 0.83" is not a defensible explanation for a regulator.
- The **re-embedding cost** on a rapidly mutating dataset outweighs the search-quality benefit.

## How

### How it works under the hood

**1. Training (the part most engineers never see).**
Modern text embedders are trained as **dual encoders** (also called bi-encoders) with a contrastive objective. Pseudocode of the InfoNCE loss for one minibatch of `N` query-document pairs `(q_i, d_i^+)`:

```python
Q = encoder(queries)        # (N, d), L2-normalized
D = encoder(documents)      # (N, d), L2-normalized
logits = Q @ D.T / tau      # (N, N), tau ~ 0.01-0.05
labels = arange(N)          # diagonal is the positive
loss = cross_entropy(logits, labels)
```

Every off-diagonal entry is an **in-batch negative**. The model is forced to make `q_i` closer to `d_i^+` than to every other document in the batch. Two refinements matter in practice:
- **Hard negatives**: BM25 or a weaker retriever mines documents that look relevant but are not, and they get appended to each batch. Without this, in-batch negatives are too easy and the model plateaus.
- **Large batches**: 1024-32768 is typical. More negatives per step = sharper gradient. This is why training serious embedders takes a lot of GPU.

Some encoders are *also* pre-trained with masked-language-modeling (MLM, BERT-style) or next-token prediction (decoder-only) before the contrastive fine-tune. The contrastive stage is what makes the pooled vector actually similarity-useful.

**2. Inference.**
Tokenize -> run through the transformer -> pool the token states into one vector -> L2-normalize -> return. For a sentence-BERT-class model on a modern GPU, throughput is a few thousand sentences/second; for a 7B-parameter embedder like `e5-mistral`, it drops to tens per second per GPU.

**3. Storage and search.**
Vectors go into an ANN index. The two dominant algorithms:
- **HNSW** (Hierarchical Navigable Small World): a multi-layer proximity graph. Query traverses from a top-layer entry point greedily downward. ~1-10 ms per query at billion scale, but the whole graph must fit in RAM (1-4 KB per vector at full precision).
- **IVF** (Inverted File): k-means clusters the space into `nlist` cells; queries probe `nprobe` nearest cells. 5-50 ms latency, 16-64x less memory than HNSW, friendlier to pre-filters. Often combined with **PQ (Product Quantization)** to compress vectors 8-32x with modest recall loss.

**4. Similarity calculation.**
For unit-normalized vectors, `cos(a, b) = a . b` — a single dot product, ~`d` multiply-adds, vectorized on SIMD/GPU. Choice of metric is largely a convention; if your model's outputs are normalized (most are), pick whatever your vector DB makes cheapest, usually dot product.

### Key trade-offs

| Design choice | Gain | Cost |
| --- | --- | --- |
| Higher `d` (3072 vs 384) | More capacity, better tail recall | 8x storage, 8x similarity cost, slower index |
| Matryoshka truncation | Choose `d` at query time | Slight recall loss vs full-dim, model must be trained for it |
| Bi-encoder vs cross-encoder | Pre-compute and ANN-search | 5-10 points lower NDCG than cross-encoder rerank |
| Contextual (BERT-class) vs static (word2vec) | Word-sense disambiguation, sentence-level meaning | 100-1000x more compute per inference |
| In-memory HNSW vs disk IVF-PQ | 10x lower latency | 10-50x more RAM |
| Cosine / dot on normalized vectors | Magnitude-agnostic | Throws away magnitude when magnitude actually means something (e.g., popularity) |

The standard production pattern is **bi-encoder retrieval + cross-encoder rerank on top-50**: cheap recall, expensive precision, best of both.

### Common failure modes
- **Anisotropy / hubness**: raw BERT `[CLS]` vectors cluster in a narrow cone; some vectors become "hubs" that look close to everything. Fix: contrastive fine-tuning, whitening, or use a sentence-BERT-class model.
- **Chunking mismatch**: chunks too long bury the relevant sentence; chunks too short lose context. Symptom: recall drops on multi-sentence questions.
- **Domain drift**: a general-web embedder underperforms on legal, biomedical, or code corpora. Fix: domain-adapted model or fine-tune.
- **Asymmetric query/doc lengths**: queries are 10 tokens, docs are 500. Symmetric models underperform; use a model trained with the right asymmetric task (most modern models support a `query:` / `passage:` prefix — getting this wrong silently halves recall).
- **Mixing model versions**: re-embedding half the corpus with a new model and searching across both. The two spaces are unrelated; results are noise.
- **Forgotten normalization**: storing un-normalized vectors and querying with cosine works; storing un-normalized vectors and querying with dot product is wrong and silently ranks by magnitude.
- **Filtered HNSW collapse**: high-selectivity filters (>90% rejected) fragment the graph and tail latency explodes. Switch to IVF or pre-filter.

## Why

### Why it exists
Computing similarity over discrete symbols is brittle: "automobile" and "car" share zero characters but mean the same thing; "bank" can mean two different things in identical strings. Embeddings exist because **geometry generalizes where exact matching cannot**. Once meaning is a coordinate, similarity is arithmetic, indexable, and scalable. The fundamental problem they address is converting an exponential, sparse symbol space into a tractable, dense continuous space where standard data-structure techniques (k-NN, clustering, linear classification) apply.

### Why it looks the way it does
Why a single dense vector instead of, say, a graph of token relationships? Because a single vector is a **fixed-size, GPU-friendly object** that any downstream consumer can handle uniformly. Multi-vector approaches like ColBERT do exist and score higher on retrieval benchmarks, but they multiply storage and search cost by sequence length — typically 30-200x. For 99% of production systems, that price is not worth the 1-3 point NDCG gain.

Why contrastive training and not just MLM? Because MLM optimizes for predicting masked tokens, not for separating semantically distinct sentences in vector space. Models trained only with MLM (raw BERT) produce vectors where most sentence pairs sit at similar cosine, regardless of meaning — the *anisotropy* problem. Contrastive training with hard negatives explicitly pushes unrelated content apart, which is exactly the geometry retrieval needs.

Why Matryoshka? Because committing to one `d` at training time forced a trade-off between quality (big `d`) and cost (small `d`). MRL trains nested objectives `L(d=8) + L(d=16) + ... + L(d=3072)` so the first `k` dimensions are themselves a valid embedding for any `k`. OpenAI's `text-embedding-3-large` at `d=256` outperforms `text-embedding-ada-002` at `d=1536` on MTEB — same model, one-sixth the storage.

### Why it matters now
RAG made embeddings load-bearing infrastructure. The MTEB leaderboard in 2026 is dominated by open-weight models (NV-Embed-v2, Qwen3-Embedding-8B, BGE-en-ICL), meaning teams can self-host top-tier retrieval without an API bill. Multimodal embeddings (Gemini Embedding 2, March 2026) are collapsing what used to be separate text-search and image-search pipelines into a single index. Vector databases have crossed into mainstream relational territory via pgvector and SQL Server's vector type. Anyone shipping an AI feature in 2026 is, transitively, shipping an embedding pipeline — and the cost-quality knobs (model choice, `d`, index type, rerank) are now where most of the wins live.

## Open questions / things to verify in practice
- Does your model use a `query:` / `passage:` prefix? Half the open-weight models do, and the docs are easy to miss. Test recall with and without.
- What is the actual P50 / P99 query latency at your target index size? HNSW numbers in blog posts assume full-RAM, no filters — measure your real workload.
- How much recall do you lose dropping from full `d` to `d/4` via Matryoshka? Worth measuring on your eval set before committing storage.
- Cosine vs dot product on your stored vectors — are they actually normalized? Print `np.linalg.norm` on a random sample.
- Hybrid search (BM25 + dense) almost always beats pure dense by 2-5 NDCG points. Have you wired up reciprocal-rank-fusion?
- How stale are your embeddings vs the live corpus? Re-embedding cadence is a silent quality knob.

Sources:
- [MTEB Leaderboard — Hugging Face](https://huggingface.co/spaces/mteb/leaderboard)
- [Matryoshka Representation Learning — Hugging Face Blog](https://huggingface.co/blog/matryoshka)
- [Contrastive Learning for Retrieval: InfoNCE & DPR](https://mbrenndoerfer.com/writing/contrastive-learning-retrieval-infonce-dpr)
- [Cosine Distance, Dot Product, Euclidean in Vector Similarity Search](https://medium.com/data-science-collective/cosine-distance-vs-dot-product-vs-euclidean-in-vector-similarity-search-227a6db32edb)
- [How to Choose Between IVF and HNSW for ANN Vector Search — Milvus](https://milvus.io/blog/understanding-ivf-vector-index-how-It-works-and-when-to-choose-it-over-hnsw.md)
- [Sentence-BERT paper / sentence-transformers](https://www.sbert.net/)
- [NVIDIA Text Embedding Model Tops MTEB Leaderboard](https://developer.nvidia.com/blog/nvidia-text-embedding-model-tops-mteb-leaderboard/)
