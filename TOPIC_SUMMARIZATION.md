# LoRA

LoRA, short for Low-Rank Adaptation, is a way to fine-tune a large pretrained model without changing any of its original weights. It freezes the model and, next to selected weight matrices, trains two thin matrices whose product is a small correction, usually well under 1% of the model's size.

It matters because full fine-tuning updates every weight: for a 7B-parameter model that means several high-memory GPUs and a full-size copy for every task. LoRA gets most of the quality on a single GPU and produces a small adapter file you can swap in and out, so one base model can serve many task-specific variants. Engineers reach for it when a model must adopt a consistent style, format, or domain vocabulary that prompting cannot reliably produce. It is not the right tool for fast-changing facts, where retrieval (RAG) works better, or when a good prompt already solves the problem.

Think of the base model as a printed encyclopedia. Full fine-tuning reprints the whole book with your edits. LoRA leaves the book alone and hands you transparent overlay sheets, one per chapter, each holding a few pencil strokes. In numbers, one 4096 by 4096 attention matrix has about 16.8 million weights, while a rank-8 LoRA adapter for it holds 65,536, about 0.4%. For deployment, the overlay can be merged into the weights so inference runs at the original speed.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/ai/lora/present/index.html
