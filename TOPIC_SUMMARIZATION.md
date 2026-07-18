# Retrieval Augmented Generation

Retrieval Augmented Generation, or RAG, is the pattern of looking things up before asking a large language model to answer. Instead of relying only on what the model memorised during training, the system first fetches the most relevant snippets from a knowledge source you control, pastes them into the prompt, and asks the model to answer using that specific context.

It matters because a plain LLM has a fixed knowledge cutoff and will confidently invent answers about anything outside it — your company's refund policy, last week's board notes, a private wiki. RAG grounds the reply in real, current, and often private documents, so the answer is traceable back to a source and far less prone to hallucination. Engineers reach for it whenever a chatbot needs to answer from private documents, whenever the facts change too often to bake into weights, and whenever a domain like medicine, law, or finance demands citations. It is chosen instead of fine-tuning when the goal is injecting new facts rather than teaching new behaviour.

Picture an open-book exam. The LLM is the student — fluent and good at synthesising an answer. Your documents are the book. RAG is the librarian sitting between them: when a question comes in, the librarian does not hand over the whole book, they flip to the two or three most relevant pages, slide them across the desk, and say "answer using these." The student then writes the essay.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/ai/retrieval-augmented-generation/
