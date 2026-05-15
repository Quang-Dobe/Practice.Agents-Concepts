# Embeddings — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS product with an "ask our docs" or "search our catalog" feature, embeddings are the layer between user queries and the corpus of content. They sit behind the API tier, in front of a vector store, and feed an LLM downstream. When latency spikes or answers go sideways, this is the layer people blame second (after the LLM) and find guilty first.

In an enterprise RAG pipeline, embeddings power the retrieval half — and retrieval, not generation, is where 70%+ of production RAG failures originate. Bad chunks in, bad answers out, no matter how good GPT-5 or Claude is at the other end.

In recommendation and de-duplication systems (catalog dedup, support-ticket clustering, fraud-ring detection), embeddings replace handcrafted feature engineering with a single dense vector per entity and a k-NN query. The "model" is barely visible; the embedding pipeline is the product.

In code search, image search, and multimodal assistants, embeddings are the only practical way to query unstructured content by intent rather than tokens. If your product has a search box and an LLM, you have an embedding pipeline whether you planned for one or not.

## Best practices

### 1. Pick the embedding model from MTEB *for your domain*, not the overall leaderboard
**Do:** Shortlist 3–5 models from MTEB's domain-relevant tasks (retrieval, clustering, code, multilingual), then run them on a custom 200–500-query holdout from your own data. Pick on recall@10 and NDCG@10, not on Twitter consensus.
**Why:** The top-of-leaderboard model is tuned for the average of 50+ tasks. On your legal/medical/code corpus, a 3rd-place general model often beats #1 by 5–10 points, and a domain-tuned model (BGE-code, ESCO, BioLORD) beats both.
**Avoid:** "We use OpenAI `text-embedding-3-large` because it's the default in the LangChain tutorial." That choice has cost real teams six-figure annual bills they didn't need.

### 2. Chunk by structure first, then by tokens — never blindly by character count
**Do:** Split on document structure (markdown headings, HTML sections, function boundaries in code) first, then enforce a token budget (typically 256–512 tokens) with a 10–20% overlap. Use a tokenizer-aware splitter, not `str.split`.
**Why:** Fixed-size character chunking shreds tables, code blocks, and multi-sentence reasoning. Adaptive/structural chunking has been measured at 87% retrieval accuracy vs ~13% for fixed-size baselines on clinical decision support.
**Avoid:** 1000-character chunks with 200-character overlap applied uniformly to PDFs, code, and chat logs.

### 3. Always run hybrid (dense + BM25), then rerank
**Do:** Retrieve top-50 with reciprocal rank fusion of dense + BM25, then rerank to top-5 with a cross-encoder (Cohere Rerank, BGE-reranker, Voyage rerank-2). Pass only the reranked top-5 to the LLM.
**Why:** Dense models miss rare entities, part numbers, SKUs, and legal citations — BM25 catches those. Reranking with a cross-encoder consistently lifts RAGAS scores by 15–30% over pure bi-encoder retrieval, and at top-50 → top-5 the added latency is ~50–150 ms.
**Avoid:** "Pure dense is enough, we're using the best model." It isn't, and the failure mode is invisible until a customer searches for "Form 1099-MISC" and gets back a generic-tax-tips doc.

### 4. Normalize once at write time, never at query time
**Do:** L2-normalize vectors before they hit the vector store. Pick dot product as the metric (cheapest). Log `np.linalg.norm` on a random sample monthly as a regression check.
**Why:** Storing un-normalized vectors and querying with dot product silently ranks by magnitude, not direction — a near-invisible bug that produces "weird but plausible" results. Forgotten normalization is one of the top three bugs in dense retrieval pipelines.
**Avoid:** Mixing normalized and un-normalized writes in the same index because one ingestion job forgot the post-processing step.

### 5. Right-size dimensions; default to Matryoshka-truncatable models
**Do:** Start at `d=768` or `d=1024`. If you outgrow storage/latency, truncate a Matryoshka model (`text-embedding-3-large`, NV-Embed, `nomic-embed-v1.5`) to `d=256` or `d=512` and re-evaluate recall.
**Why:** Storage scales linearly in `d`, similarity compute scales linearly in `d`, and most non-tail queries don't need 3072 dimensions. OpenAI's `-3-large` at d=256 beats `ada-002` at d=1536 on MTEB. Halving `d` halves your vector-DB bill.
**Avoid:** Picking `d=3072` because "more capacity sounds better" and then discovering at 50M vectors that you've quadrupled your RAM budget.

### 6. Match the query/passage prefix the model was trained with
**Do:** Read the model card. For `e5`, `bge`, `nomic`, prepend `query:` to queries and `passage:` to documents (or the equivalents). Encode test vectors and confirm cosine on a known pair matches the model card's example.
**Why:** Asymmetric models trained with prefixes silently lose ~30–50% of recall when you forget them. The pipeline appears to work; quality is just quietly halved.
**Avoid:** Copy-pasting an OpenAI embedding call and swapping in `bge-large-en-v1.5` without reading its instructions.

### 7. Tune HNSW (or pick IVF) for your actual workload
**Do:** For HNSW, start with `M=16, ef_construction=200, ef_search=64`, then sweep `ef_search` against your eval set. For corpora >10M vectors with tight RAM, use IVF-PQ with `nlist≈sqrt(N)` and `nprobe` tuned to recall target. Re-benchmark after every 10x growth.
**Why:** Default vector-DB knobs target generic benchmarks. Your recall and P99 latency are sitting on a curve you haven't measured. Filtered queries can collapse HNSW performance entirely (>90% filter selectivity fragments the graph).
**Avoid:** Treating the vector DB as a black box. "It worked in staging at 100k vectors" is not a load test for 50M.

### 8. Pick the vector store for *operational* fit, not the leaderboard
**Do:** pgvector if you're already on Postgres and <10–50M vectors — keep one operational story. Qdrant for self-hosted with heavy metadata filtering. Milvus at billion scale. Pinecone when you want zero ops and budget isn't the bottleneck. Weaviate if you want native hybrid out of the box. FAISS only as an in-process library, never as your "database."
**Why:** The biggest hidden cost is not query latency — it's a second backup story, a second IAM model, a second on-call rotation. Engineering hours dwarf compute cost until ~50M vectors.
**Avoid:** Pinecone for a 200k-vector prototype paying $70/month when one `CREATE EXTENSION vector` would do.

### 9. Plan for re-embedding from day one
**Do:** Store the source text, the chunk boundaries, the model name, and the model version next to every vector. Treat the index as a *cache* of a deterministic function of (text, model). Build a backfill job before you have a reason to need one.
**Why:** You will switch models. Every embedding pipeline has eventually migrated — `ada-002` → `-3-small`, `bge-base` → `bge-large`, generic → domain-tuned. Mixing two model versions in one index produces noise, not results, and re-embedding 100M vectors without a tested job is a week-long incident.
**Avoid:** "We'll just re-embed when we need to." Without provenance metadata, you can't even tell which vectors are stale.

### 10. Evaluate with a golden set, not vibes
**Do:** Maintain a 200–1000-row golden set of (query, expected_doc_id_or_chunk) pairs from real user logs. Measure recall@10, MRR, and NDCG@10 on every model/chunking/index change. Gate deploys on regressions.
**Why:** Embedding changes are silent — quality drifts without errors, alerts, or stack traces. "It feels worse" arrives as a customer escalation three weeks later. A golden set turns a vibes-debate into a number.
**Avoid:** Relying solely on MTEB scores. Public benchmarks correlate with — but never replace — your corpus.

### 11. Cache aggressively, but key on `(text_hash, model, version)`
**Do:** Hash the normalized input text + model identifier + model version, store the resulting vector in Redis or a KV store with a long TTL. Same query string, same model = no API call.
**Why:** Embedding API calls are the single biggest line item in many RAG bills. Up to 60–80% of production queries are near-duplicates of recent queries (autocomplete, retries, refresh). Caching cuts cost and latency in one move.
**Avoid:** Caching by raw text only. A model version bump silently serves stale vectors and corrupts retrieval.

### 12. Guard against embedding leakage in evaluation
**Do:** When building eval sets, split by *document* or *time*, not by chunk. Make sure no chunk from a training/eval document appears on both sides.
**Why:** Chunk-level splits leak near-duplicate passages between train and test, inflating recall by 10–30 points and producing a model that looks great offline and disappoints in prod.
**Avoid:** Random row-level shuffles on chunk tables. The model is memorizing, not generalizing.

## Anti-patterns to recognize

- **Chunk-and-pray**: Splitting every document into 1000-char chunks with no regard for structure, then hoping retrieval works. It fails on tables, code, and any multi-sentence reasoning. Use structural chunking with a token budget instead.
- **Mixing model versions in one index**: Re-embedding half the corpus with a new model and querying across both spaces. The vector spaces are unrelated; results are noise. Always full-backfill behind a feature flag.
- **Cosine on un-normalized vectors**: Storing raw outputs, querying with dot product, and assuming it's equivalent to cosine. It silently ranks by magnitude. Normalize at write time, period.
- **Choosing the vector DB by leaderboard QPS**: Picking Milvus because a benchmark blog said so when you have 200k vectors and one engineer. The operational overhead dwarfs the perf gain. Match the tool to your scale.
- **No reranker**: Trusting the bi-encoder's top-5 directly. The top-5 is noisier than the top-50; a cross-encoder rerank turns noise into precision. Add it before you tune anything else.
- **Treating chunks as the source of truth**: Storing only the chunk text, not the parent document ID or offsets. When you need to re-chunk (you will), you can't. Always keep a pointer to the original.
- **Skipping the query/passage prefix**: Using `e5`/`bge`/`nomic` without the model's required asymmetric prefix. Halves recall, no error message, ships to prod undetected.
- **Embedding the entire 10 MB PDF**: Passing whole documents to an embedding endpoint, hitting context limits, getting truncated silently. Chunk first, embed second, log token counts.

## Real-world usage patterns

**Enterprise support knowledge base (mid-size SaaS).** ~500k support articles + tickets, pgvector inside the existing Postgres, BGE-large embeddings at d=1024, hybrid search via Postgres FTS + pgvector with RRF, Cohere rerank-3 on top-50. Latency P99 ~300 ms. Non-obvious lesson: the biggest quality jump came from chunking by markdown heading (not from a better model). Switching from a 1000-char splitter to a heading-aware one moved NDCG@10 from 0.61 to 0.78.

**Multilingual e-commerce search (mid–large retailer).** ~20M products, multilingual queries across 12 languages. Cohere `embed-multilingual-v4` at d=1024 in Qdrant with payload filters on category/region/inventory. Non-obvious lesson: pre-filtering on `in_stock=true` at the vector store level (not post-filter) reduced wasted retrieval work by 70% — but only after switching from HNSW to IVF, because high-selectivity filters were fragmenting the HNSW graph.

**Code search inside a monorepo (developer platform team).** ~30M code chunks, function-level chunking, `voyage-code-3` embeddings. Stored in Qdrant with `language`, `repo`, `path` filters. Non-obvious lesson: function-level chunking outperformed file-level by ~20 NDCG points, but only when class context (parent class signature) was prepended to each method chunk — pure function bodies lost crucial context.

**RAG-powered legal assistant (vertical AI startup).** ~5M case-law passages, domain-tuned embedding model fine-tuned with contrastive triples from case citations, Milvus at d=768, BM25-first hybrid with dense as a re-scorer (not the other way around — lexical citation matching is critical). Non-obvious lesson: in legal text, BM25 outranks dense for ~40% of queries (statute numbers, party names). RRF with weight tilted toward BM25 beat the dense-heavy default convincingly.

## Operational checklist

- **Monitoring**: Are you tracking embed-API P50/P99 latency, vector-DB query P50/P99, recall@10 on a golden set (weekly), and cache hit rate?
- **Failure handling**: When the embedding API is down, does the pipeline fall back to BM25-only retrieval or fail hard? Is that documented and tested?
- **Model provenance**: Does every stored vector carry `(model_name, model_version, embedded_at)` metadata, and can you query for stale rows?
- **Re-embedding playbook**: Is there a tested backfill job that can re-embed the corpus behind a feature flag without downtime?
- **Security**: Are you stripping PII before sending text to a third-party embedding API? Are vectors themselves treated as PII (they can leak training-set content)?
- **Cost**: Do you have a per-tenant cap on embed-API spend? Is the cache hit rate >50%? Is `d` justified by an actual recall measurement?
- **Filter selectivity**: For your top 10 query shapes, what fraction of vectors survive metadata filters? If >90% are filtered out, are you on IVF, not HNSW?
- **Onboarding**: Can a new engineer find (a) which model is in use, (b) where the eval set lives, (c) how to run an offline retrieval benchmark — in under 30 minutes?
- **Drift**: Is there a scheduled job comparing recall@10 today vs 30 days ago on the golden set? Drift is silent without it.

## How this topic typically evolves in a codebase

Teams start with the "tutorial stack": OpenAI `text-embedding-3-small` + Chroma or pgvector + a single-tenant chunker that splits on 1000 characters. This works wonderfully for the demo, the design partner, and the first ~50k documents. Costs are negligible, latency is acceptable, and the team ships.

The painful migration usually arrives in three waves. First, retrieval quality plateaus and a customer escalation forces the team to add a reranker, then hybrid search, then structural chunking — each is a meaningful refactor. Second, the corpus crosses ~5M vectors and the vector DB starts to hurt: pgvector index builds take hours, or Pinecone's bill becomes visible to the CFO. This triggers a vector-store migration, which is painful precisely because nobody wrote down the model version per vector. Third, the team decides to switch embedding models (cost, quality, or domain fit) and has to build the re-embedding pipeline they should have built on day one.

Mature embedding stacks end up looking similar across companies: structural chunking with model-version metadata, hybrid retrieval with a tuned rerank, a self-hosted or pgvector store sized to the corpus, a golden eval set wired into CI, and a cache layer in front. The teams that get there fastest are the ones who treated the index as a cache of `(text, model_version)` from the start.

## Further reading

- [MTEB Leaderboard — Hugging Face](https://huggingface.co/spaces/mteb/leaderboard) — the only honest cross-model comparison. Filter by your task before trusting any number.
- [Pinecone Learn: Hybrid Search and Reranking](https://www.pinecone.io/learn/hybrid-search-intro/) — the canonical explanation of RRF and rerank, vendor-neutral enough to apply to any store.
- [Anthropic's Contextual Retrieval](https://www.anthropic.com/news/contextual-retrieval) — the "prepend context to each chunk" trick that drops retrieval failure rates by ~50% on real corpora.
- [Building Production RAG: Chunking, Evaluation & Monitoring (2026)](https://blog.premai.io/building-production-rag-architecture-chunking-evaluation-monitoring-2026-guide/) — pragmatic, recent, covers what most tutorials skip.
- [Choosing a Vector Database — Firecrawl](https://www.firecrawl.dev/blog/best-vector-databases) — a current and fair comparison of pgvector, Pinecone, Qdrant, Milvus, Weaviate at production scale.
- [Sentence-Transformers Documentation](https://www.sbert.net/) — still the best practical reference for how bi-encoders, cross-encoders, and rerankers fit together.
