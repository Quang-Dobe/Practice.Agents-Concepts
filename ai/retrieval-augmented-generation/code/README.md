# Retrieval Augmented Generation — MVP Code

The smallest runnable demo of RAG. About 60 lines of actual code, comments excluded.

## What it demonstrates

- The two-stage RAG pipeline: `retrieve` then `generate`, decoupled in one file.
- Offline indexing vs. online query — the corpus is embedded once at import; each query only reads the index.
- Top-k cosine similarity as the retrieval primitive, plus the refusal guardrail that fires when retrieval confidence is low (see `../docs/03-practice.md § 8`).

## Run it

Python 3.11+, no third-party packages, no network, no API keys. `python3 mvp.py`.

## Expected output

Three demo queries, each printing top-3 retrieved chunks with cosine scores, the assembled prompt, and the simulated answer. Trimmed sample:

```
QUERY: How long can I return the King Serenity mattress?
  0.417  The King Serenity mattress can be returned within 180 nights ...
  0.167  Mattress covers are machine-washable in cold water ...
--- Simulated LLM answer ---
The King Serenity mattress can be returned within 180 nights of delivery for a full refund.
```

Query 3 ("Do you sell pillows?") triggers the refusal path.

## Caveat

The embedder is a **toy**: a deterministic hashed bag-of-words. It matches on shared *words*, not on meaning — so "cancel my subscription" would not retrieve a chunk about "termination of service". In production, swap `embed()` for a real model (`sentence-transformers`, OpenAI `text-embedding-3-small`, Cohere `embed-v4`, BGE) and swap the simulated generator for a real LLM call. The retrieve -> prompt-assemble -> generate flow is the point.

## What to try next

- Add a document to `CORPUS` and re-run — no reindex step is needed.
- Lower `CONFIDENCE_THRESHOLD` to `0.05` and watch query 3 start hallucinating.
- Rephrase query 1 as "When can I send back my mattress?" — the toy embedder misses it; a real one wouldn't.
