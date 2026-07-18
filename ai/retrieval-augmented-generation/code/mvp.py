"""RAG MVP — retrieve, then generate.

The smallest end-to-end demo of the Retrieval Augmented Generation pattern:
    1. A tiny in-memory corpus (a fictional mattress-company support KB).
    2. A deterministic hash-based embedder (no network, no API keys).
    3. Cosine top-k retrieval.
    4. A `generate` step that assembles a prompt and simulates an LLM answer.

In production you would swap the toy embedder for a real one
(`sentence-transformers`, OpenAI `text-embedding-3-small`, Cohere `embed-v4`,
BGE, etc.) and swap the simulated generator for a real LLM call. The
retrieval -> generation handoff — which *is* the RAG pattern — stays the same.
"""

from __future__ import annotations

import math
import re
from hashlib import blake2b


# --- Corpus ---------------------------------------------------------------
# A tiny fictional support KB. In production this is thousands of chunks
# loaded from PDFs / Confluence / a wiki and persisted in a vector store
# (pgvector, Qdrant, Pinecone, Weaviate, FAISS...). Here we keep it in a list
# so the whole flow — embed, index, retrieve, generate — fits on one screen.
CORPUS: list[str] = [
    "The King Serenity mattress can be returned within 180 nights of delivery for a full refund.",
    "The Queen Cloud mattress ships free within the continental United States and arrives in 3-5 business days.",
    "All Serenity-line mattresses come with a 10-year limited warranty covering sagging deeper than 1.5 inches.",
    "Financing is available through Affirm at 0% APR for orders over $500 with approved credit.",
    "Mattress covers are machine-washable in cold water on the gentle cycle; do not tumble dry.",
    "Customer support is reachable at support@serenity.example from 9am to 6pm Eastern, Monday through Friday.",
    "The Twin Dreamer is our entry-level model and is not eligible for the 180-night trial.",
]


# --- Embedder -------------------------------------------------------------
# Hashed binary bag-of-words: tokenize -> hash each unique token into one of
# DIM buckets -> L2-normalize. Deterministic (blake2b, not Python's salted
# hash) so the same text always yields the same vector across runs. We use
# set semantics (presence, not count) so common words like "the" don't
# dominate short chunks. Good enough for a handful of documents; a real
# embedding model would map semantic similarity ("cancel my order" ~
# "termination of service") into vector space — this toy embedder is just
# a normalized keyword-presence vector, so it still misses paraphrases and
# stemming variants like "return" vs "returned".
DIM = 4096


def _tokens(text: str) -> list[str]:
    return re.findall(r"[a-z0-9]+", text.lower())


def embed(text: str) -> list[float]:
    v = [0.0] * DIM
    for tok in set(_tokens(text)):
        # 4-byte blake2b digest -> stable integer -> bucket index.
        h = int.from_bytes(blake2b(tok.encode(), digest_size=4).digest(), "big")
        v[h % DIM] = 1.0
    norm = math.sqrt(sum(x * x for x in v)) or 1.0
    return [x / norm for x in v]


def cosine(a: list[float], b: list[float]) -> float:
    # Both vectors are L2-normalized, so dot product == cosine similarity.
    return sum(x * y for x, y in zip(a, b))


# Pre-embed the corpus once at startup. In a real system this is an offline
# batch job that writes vectors + metadata to an ANN index; the online query
# path only reads from that index.
INDEX: list[tuple[str, list[float]]] = [(doc, embed(doc)) for doc in CORPUS]


# --- Retrieve -------------------------------------------------------------
def retrieve(query: str, k: int = 3) -> list[tuple[float, str]]:
    """Return the top-k (score, document) pairs by cosine similarity."""
    q = embed(query)
    scored = [(cosine(q, doc_vec), doc) for doc, doc_vec in INDEX]
    scored.sort(key=lambda pair: pair[0], reverse=True)
    return scored[:k]


# --- Generate -------------------------------------------------------------
# In production this step calls an LLM (GPT-4o, Claude, Gemini, Llama...)
# with a prompt that pins the model to the retrieved context. Here we
# assemble the same prompt shape and then *simulate* the LLM: if retrieval
# was confident we surface the top chunk as the answer; if not we refuse.
# That refusal is the exact guardrail a well-prompted LLM should follow.
PROMPT_TEMPLATE = """You are a support assistant. Answer using ONLY the context below.
If the context does not contain the answer, reply: "I don't know based on the provided documents."

Context:
{context}

Question: {query}
Answer:"""

# Cosine threshold below which we treat retrieval as a miss. Tuned by eye
# for this toy embedder; a real system tunes it against a golden Q/A set.
CONFIDENCE_THRESHOLD = 0.20


def generate(query: str, retrieved: list[tuple[float, str]]) -> str:
    context = "\n".join(f"[{i + 1}] {doc}" for i, (_, doc) in enumerate(retrieved))
    prompt = PROMPT_TEMPLATE.format(context=context, query=query)

    top_score, top_doc = retrieved[0]
    if top_score < CONFIDENCE_THRESHOLD:
        simulated_answer = "I don't know based on the provided documents."
    else:
        simulated_answer = top_doc

    return (
        "--- Prompt that would be sent to the LLM ---\n"
        f"{prompt}\n\n"
        "--- Simulated LLM answer ---\n"
        f"{simulated_answer}"
    )


# --- Demo ----------------------------------------------------------------
if __name__ == "__main__":
    queries = [
        # Direct hit: keywords overlap strongly with one chunk.
        "How long can I return the King Serenity mattress?",
        # Second hit: tests that a different chunk wins for a different query.
        "What is the warranty on Serenity mattresses?",
        # Miss: nothing in the corpus talks about pillows. The refusal path
        # is what separates a grounded RAG system from a hallucinating one.
        "Do you sell pillows?",
    ]
    for q in queries:
        print("=" * 72)
        print(f"QUERY: {q}\n")
        hits = retrieve(q, k=3)
        print("Top-3 retrieved chunks (cosine score, text):")
        for score, doc in hits:
            print(f"  {score:.3f}  {doc}")
        print()
        print(generate(q, hits))
        print()
