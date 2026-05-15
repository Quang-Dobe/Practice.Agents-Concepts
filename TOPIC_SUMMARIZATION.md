# Embeddings

An embedding is a fixed-length list of numbers — a vector — that places a piece of meaning at a specific spot on a giant invisible map. A model is trained so that inputs with related meaning land near each other in that space and unrelated inputs land far apart. Once everything is a vector, the question "is this similar to that?" becomes a distance calculation: fast, mechanical, and easy to scale.

Engineers reach for embeddings whenever the goal is to compare things by meaning rather than by exact tokens. Semantic search that finds "exception handling" when the user typed "how do I stop my code from crashing," retrieval-augmented generation that pulls the right document chunks before an LLM answers, clustering customer feedback, deduplicating near-identical content, "more like this" recommendations, and lightweight text classification all sit on top of the same primitive. The catch is that embeddings are not deterministic across models — vectors from one model are meaningless to another — and they are a poor choice when the task needs exact matches, structured filters, or explanations a regulator will accept.

A useful analogy is a library where books are not shelved alphabetically but placed at coordinates in a vast warehouse. Cookbooks cluster in one corner, mysteries in another, and a half-memoir half-cookbook floats between them. To find books like the one in your hand, you do not read titles — you walk a small circle around its location and grab whatever you bump into. That warehouse is the embedding space, the coordinates are the embedding vector, and the librarian is the embedding model.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/ai/embeddings/
