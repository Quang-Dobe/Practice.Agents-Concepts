# Embeddings — Overview

> An embedding is a fixed-length list of numbers that places a piece of meaning at a specific spot on a giant invisible map, so that "near each other" means "similar in meaning."

## The 30-second version
An embedding turns a word, sentence, image, or any chunk of data into a vector — typically a few hundred to a few thousand floating-point numbers. A model is trained so that inputs with related meaning land near each other in that vector space, and unrelated inputs land far apart. Once everything is a vector, "is this similar to that?" becomes a distance calculation — fast, mechanical, and easy to scale. This is the trick that powers semantic search, retrieval-augmented generation (RAG), recommendations, clustering, and most of the "the AI just understood what I meant" magic of modern systems.

## The mental model
Imagine a library, but instead of shelving books alphabetically, the librarian places every book at coordinates in a vast 3D warehouse (now imagine 1,536D — same idea, more directions). Cookbooks cluster in one corner. Murder mysteries cluster in another. A book that is half-cookbook, half-memoir floats between the two. To find books "like this one," you don't read titles — you walk in a small circle around its location and grab whatever you bump into.

That warehouse is the embedding space. The coordinates are the embedding vector. The librarian is the embedding model — usually a transformer like OpenAI's `text-embedding-3-small`, Cohere's embed models, or an open-source one like `bge` or `nomic-embed`. You hand it text, it hands back coordinates.

The deep part: nobody hand-picked what each axis means. The model learned, from billions of examples, that "king" and "queen" should sit near each other, and that the vector from "man" to "woman" should point in roughly the same direction as "king" to "queen." Meaning becomes geometry.

## What it is NOT
- Not a database. Embeddings are the *values*; a vector database (Pinecone, pgvector, Qdrant) is where you *store and search* them.
- Not the same as tokens. Tokens are the chopped-up input pieces an LLM reads; embeddings are the semantic coordinates of pieces or whole texts.
- Not one-hot encoding or TF-IDF. Those are sparse, keyword-based representations. Embeddings are dense and meaning-based — "automobile" and "car" land next to each other; in TF-IDF they look unrelated.
- Not deterministic across models. The vector from one model is meaningless to another. You cannot mix `text-embedding-3-small` outputs with `bge-large` outputs.

## When you would reach for it
- You want search that understands intent, not just keywords ("how do I stop my code from crashing" finds a doc titled "exception handling").
- You are building RAG and need to pull the right context chunks for an LLM.
- You want to cluster customer feedback, deduplicate near-identical content, or recommend "more like this."
- You want to classify text without training a classifier — just compare to labeled example vectors.

## When you would NOT reach for it
- You need exact matches, IDs, or structured filters — use SQL or a regular index.
- Your dataset is tiny (a few hundred items). A keyword search and a coffee break will do.
- You need explainability for a regulator. "It was geometrically close" is a hard sell.
- Your data changes by the second and re-embedding cost outweighs the search benefit.

## Key vocabulary (just enough to keep reading)
- **Vector / embedding**: the list of numbers itself.
- **Dimension**: the length of that list (e.g., 768, 1536, 3072).
- **Embedding model**: the neural net that produces the vector.
- **Cosine similarity**: the most common "how close are these two vectors" measure; 1.0 means identical direction.
- **Vector database**: storage optimized for nearest-neighbor search over millions of vectors.
- **ANN (Approximate Nearest Neighbor)**: the fast-but-not-perfect search algorithms (HNSW, IVF) that make vector search scale.
- **Chunking**: splitting long text into smaller pieces before embedding, so each vector represents a coherent thought.
- **Semantic search**: the canonical use case — retrieve by meaning, not by keyword.

## What's next
The next document answers What / Where / When / How / Why in detail — including how embedding models are trained, how cosine similarity actually works, how to choose dimensions and chunk sizes, and how vectors get indexed for search at scale.

Sources:
- [Embeddings 101 — Data Science Dojo](https://datasciencedojo.com/blog/embeddings-and-llm/)
- [How Embeddings Extend Your AI Model's Reach — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/ai/conceptual/embeddings)
- [The Building Blocks of LLMs — The New Stack](https://thenewstack.io/the-building-blocks-of-llms-vectors-tokens-and-embeddings/)
