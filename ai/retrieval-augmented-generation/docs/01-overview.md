# Retrieval Augmented Generation — Overview

> RAG is the pattern of looking things up first and then asking the LLM to answer using only what you found.

## The 30-second version

An LLM on its own is a very well-read intern who has been locked in a room since their training data was frozen. Ask them about your company's refund policy or last week's board meeting notes and they will either say "I don't know" or, worse, invent something that sounds right. RAG fixes this by giving the model a research step: before answering, the system fetches the most relevant snippets from a knowledge source you control, pastes them into the prompt, and asks the model to answer from that. The result is grounded, up-to-date, and traceable back to a source document.

## The mental model

Picture an open-book exam. The student is the LLM: fluent, articulate, good at synthesizing an answer. The book is your knowledge base: your product docs, your Confluence, your PDFs, your Slack export, your SQL rows dumped to text. RAG is the librarian sitting between them. When a question comes in, the librarian does not hand over the whole book — they flip to the two or three most relevant pages, slide them across the desk, and say "answer using these." The student writes the essay.

Concretely, imagine a customer-support bot for a mattress company. A user asks, "Can I return the King Serenity mattress after 120 nights?" A vanilla LLM guesses. A RAG bot does this:

1. **Retrieve.** It searches the company's return-policy documents, finds the paragraph that reads "King Serenity returns are accepted within 180 nights of delivery," and grabs it.
2. **Generate.** It sends the model a prompt shaped like: *"Using the context below, answer the user. Context: <that paragraph>. Question: <user's message>."*

The model replies, "Yes — you have up to 180 nights, so 120 is fine," and can cite the exact policy page it used. No hallucination, no stale training data, no fine-tuning required.

## What it is NOT

- **Not fine-tuning.** Fine-tuning bakes new *behavior* into the model's weights. RAG leaves the weights alone and injects new *facts* at query time.
- **Not just "long context."** Stuffing an entire 500-page manual into the prompt is expensive, slow, and often less accurate than retrieving the right two pages.
- **Not a search engine.** A search engine returns links for a human to read. RAG uses retrieval as a hidden intermediate step so the model can write the final answer.
- **Not a memory system for an agent.** Related, but agent memory is about remembering past turns; RAG is about grounding in a knowledge corpus.

## When you would reach for it

- A chatbot that must answer from your private documents (support KB, internal wiki, legal contracts).
- Any assistant that needs facts newer than the model's training cutoff.
- A domain expert bot (medical, legal, financial) where making things up is unacceptable and citations are required.
- Search UIs that want a natural-language answer on top of results, not just a link list.

## When you would NOT reach for it

- The knowledge fits in the system prompt and never changes — just paste it in.
- The task is pure reasoning or code generation with no external facts (e.g., "refactor this function").
- You need the model to adopt a *style* or *format* consistently — that is a fine-tuning problem, not a retrieval one.
- Latency budget is under ~100 ms and you cannot afford the extra retrieval hop.

## Key vocabulary (just enough to keep reading)

- **Corpus** — the collection of documents you want the model to answer from.
- **Chunk** — a small slice of a document (a paragraph or two) that gets retrieved as a unit.
- **Embedding** — a numeric vector that represents the meaning of a chunk, used for semantic search.
- **Vector store** — a database optimized for finding chunks whose embeddings are closest to a query.
- **Retriever** — the component that turns a user question into a set of relevant chunks.
- **Context window** — the maximum amount of text the LLM can read in a single call.
- **Grounding** — anchoring a generated answer in retrieved source material.
- **Hallucination** — a confident, fluent, and wrong answer produced when the model has no real basis for what it is saying.

## What's next

`02-deep-dive.md` walks through the full retrieve-then-generate pipeline in detail: how documents get chunked and embedded, how the vector store finds nearest neighbors, how the prompt is assembled, and where the common failure modes live.
