"""
Embeddings MVP — the smallest demo that makes the core idea concrete.

What this script proves:
  1. An embedding model turns text into a fixed-length vector.
  2. Semantically related sentences land near each other in that vector
     space (high cosine similarity), even when they share zero words.
  3. Once everything is a vector, "search" becomes a distance calculation:
     a query vector vs. every doc vector, ranked by cosine.

We use sentence-transformers/all-MiniLM-L6-v2: 384-dim, contrastive-tuned,
already L2-normalized on output. ~80 MB, runs on CPU in seconds.
"""

from sentence_transformers import SentenceTransformer
import numpy as np

# The model is the "librarian" — the same neural net must encode both
# corpus and queries, otherwise their vectors live in unrelated spaces.
# all-MiniLM-L6-v2 is symmetric (no query:/passage: prefix needed).
model = SentenceTransformer("all-MiniLM-L6-v2")

# A tiny, deliberately diverse corpus. Two clusters by meaning:
#   - cars / vehicles
#   - cooking / food
# Note that "automobile" and "car" share no characters with each other's
# paraphrases — keyword search would miss the link; embeddings will not.
corpus = [
    "The new electric car has a 400-mile range on a single charge.",
    "Modern automobiles are increasingly powered by lithium-ion batteries.",
    "Highway driving drains EV batteries faster than city traffic.",
    "I baked a sourdough loaf with a crispy crust this morning.",
    "Roasting garlic before adding it to pasta deepens the flavor.",
    "A sharp chef's knife is the most important tool in any kitchen.",
]

# encode() returns shape (N, d). normalize_embeddings=True is the
# write-time normalization the practice doc warns about — do it once,
# here, so dot product == cosine for every downstream comparison.
doc_vectors = model.encode(corpus, normalize_embeddings=True)
print(f"Encoded {len(corpus)} docs into shape {doc_vectors.shape}\n")

# --- Demo 1: pairwise similarity reveals the two semantic clusters ---
# With normalized vectors, cosine similarity is just the matrix product.
# Expect a visible block structure: high values inside each topic, low
# values across topics.
sim_matrix = doc_vectors @ doc_vectors.T
print("Pairwise cosine similarity (rows/cols = doc index):")
with np.printoptions(precision=2, suppress=True):
    print(sim_matrix)
print()

# --- Demo 2: semantic search ---
# A query that shares almost no vocabulary with the corpus. Keyword
# search would return nothing; embedding search should still surface
# the car-cluster docs because the *meaning* is close.
queries = [
    "How far can EVs go before recharging?",
    "Tips for making bread at home",
]

for query in queries:
    # Same model, same normalization — the query vector must live in the
    # same space as the doc vectors.
    q_vec = model.encode(query, normalize_embeddings=True)

    # One dot product per doc. At a million docs you'd hand this to an
    # ANN index (HNSW / IVF); at six docs, brute force is the right tool.
    scores = doc_vectors @ q_vec

    # argsort descending — top match first.
    top = np.argsort(-scores)[:3]

    print(f"Query: {query!r}")
    for rank, idx in enumerate(top, start=1):
        print(f"  {rank}. score={scores[idx]:.3f}  {corpus[idx]}")
    print()
