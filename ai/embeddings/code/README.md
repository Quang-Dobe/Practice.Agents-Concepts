# Embeddings — MVP Code

The smallest runnable demo of embeddings. About 30 lines of actual Python, comments excluded.

## What it demonstrates

- An embedding model (`all-MiniLM-L6-v2`, 384-dim) turns sentences into fixed-length vectors.
- Cosine similarity over normalized vectors reveals two semantic clusters (cars vs cooking) — directly from geometry, with no keyword overlap required.
- Semantic search reduces to one dot-product per document: a query like "How far can EVs go before recharging?" retrieves the EV-range sentence even though they share almost no words.
- Write-time L2 normalization (`normalize_embeddings=True`) is what makes dot product equal to cosine — the practice doc's rule made concrete.

## Prerequisites

- Python 3.11+
- One install:

```bash
pip install sentence-transformers
```

First run downloads the model (~80 MB) into `~/.cache/huggingface`. Subsequent runs are offline.

## Run it

```bash
python mvp.py
```

## Expected output

```
Encoded 6 docs into shape (6, 384)

Pairwise cosine similarity (rows/cols = doc index):
[[1.   0.55 0.50 0.05 0.07 0.04]
 [0.55 1.   0.42 0.06 0.08 0.05]
 ...
 [0.04 0.05 0.04 0.28 0.30 1.  ]]

Query: 'How far can EVs go before recharging?'
  1. score=0.61  Highway driving drains EV batteries faster than city traffic.
  2. score=0.55  The new electric car has a 400-mile range on a single charge.
  3. score=0.40  Modern automobiles are increasingly powered by lithium-ion batteries.
```

Exact scores vary by hardware/version; the *ranking* is stable.

## What to try next

- Add a sentence about motorcycles and see whether it joins the car cluster or sits between.
- Remove `normalize_embeddings=True` from both `encode` calls — the matrix changes and ranking can subtly break.
- Swap the model for `BAAI/bge-small-en-v1.5` and prepend `"query: "` to queries; compare top-1 quality.
- Add 1,000 synthetic docs and time the search loop — you'll feel why ANN indexes (HNSW, IVF) exist.
