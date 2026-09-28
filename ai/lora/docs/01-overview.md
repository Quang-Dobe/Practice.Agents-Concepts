# LoRA — Overview

> LoRA (Low-Rank Adaptation) fine-tunes a large pretrained model by freezing all of its original weights and training a small add-on correction per layer. That add-on is usually well under 1% of the model's size.

## The 30-second version

Full fine-tuning updates every weight in a model. For a 7B-parameter LLM that means gradients and optimizer state for all 7 billion numbers, several high-memory GPUs, and a full-size copy of the model for every task. LoRA leaves the base model untouched. Next to selected weight matrices, it trains two thin matrices whose product is a small "delta". You get most of the quality of full fine-tuning, you can train on a single GPU, and the result is an adapter file of a few megabytes to a few hundred megabytes that you can swap in and out. The original paper ([Hu et al., 2021](https://arxiv.org/abs/2106.09685)) reports that on GPT-3 175B it trained about 10,000x fewer parameters and used about 3x less GPU memory than full fine-tuning.

## The mental model

Think of the base model as a **printed encyclopedia**. It is huge, expensive to reprint, and mostly correct for your needs. You want it to write in your company's style, or know your product's jargon.

- **Full fine-tuning** reprints the whole encyclopedia with your edits worked into every page. You get a second full-size copy, and one more for every other customer.
- **LoRA** leaves the printed book alone and hands you a **stack of transparent overlay sheets**, one per chapter. Each sheet holds only a few pencil strokes. When you read a chapter with its overlay on top, you see the original text plus the corrections.

Why can a few strokes be enough? The insight behind LoRA is that the *change* a fine-tune needs is simple, even though the model is not. In math terms, the update to each weight matrix has **low rank**. It can be written as a tall-thin matrix times a short-wide matrix:

```text
W_effective = W_frozen + B · A
              (d × d)    (d × r)(r × d),  r is small (e.g. 8 or 16)
```

Concrete numbers: one 4096 × 4096 attention matrix has about 16.8M weights. With rank `r = 8`, `B` and `A` together hold 8 × (4096 + 4096) = 65,536 weights, about 0.4% of the original.

The overlays can also be **peeled off or swapped**. One base model can serve a "legal" adapter, a "support-bot" adapter and a "SQL" adapter. For deployment you can also **merge** an overlay permanently into the weights (`W + BA`), so inference runs at exactly the original speed.

## What it is NOT

- Not full fine-tuning. Full fine-tuning updates every weight. LoRA updates none of the original ones.
- Not RAG. RAG adds knowledge at query time by retrieving documents. LoRA changes behavior by training weights.
- Not prompt engineering or prompt tuning. Those steer the model through its input. LoRA modifies the internal layers.
- Not quantization. Quantization shrinks the model's numbers to save memory. QLoRA *combines* the two: a 4-bit frozen base plus LoRA adapters.
- Not a model architecture. LoRA is a training technique you apply to an existing transformer (or diffusion model, CNN, and so on).

## When you would reach for it

- You need a model to adopt a consistent style, format or domain vocabulary that prompting can't reliably produce.
- You have one or a few consumer or cloud GPUs, not a training cluster.
- You want many task-specific variants of one base model without storing many full copies.
- You are customizing an open-weights model (Llama, Mistral, Qwen, Stable Diffusion) and want a small, shareable artifact.
- You want to iterate quickly: train, evaluate, throw away, retrain, all in hours.

## When you would NOT reach for it

- The model lacks *facts* that change often. Use RAG. Fine-tuning is a poor, stale database.
- A well-written prompt or a few examples already solve the problem.
- You only have API access to a closed model, with no weights to adapt. Use the vendor's fine-tuning service if it has one.
- You are teaching a fundamentally new capability or language at scale, where full fine-tuning or continued pretraining still wins.
- You have fewer than a few hundred good examples. The adapter will likely overfit or learn noise.

## Key vocabulary (just enough to keep reading)

- **Base model**: the pretrained model whose weights stay frozen.
- **Adapter**: the trained LoRA matrices (`A` and `B`), saved separately from the base.
- **Rank (`r`)**: the inner dimension of `B · A`. Higher means more capacity and more parameters. Common values are 4–64.
- **Alpha (`lora_alpha`)**: a scaling factor for how strongly the adapter's delta is applied (effective scale `alpha / r`).
- **Target modules**: which layers get adapters, typically the attention projections (`q_proj`, `v_proj`, …) and sometimes the MLP layers.
- **Merging**: folding `B · A` into `W` so the deployed model has no extra layers or latency.
- **PEFT**: Parameter-Efficient Fine-Tuning, the family LoRA belongs to. It is also the name of Hugging Face's [`peft`](https://huggingface.co/docs/peft) library.
- **QLoRA / DoRA**: popular variants. QLoRA keeps the base model in 4-bit to save memory. DoRA splits each weight into magnitude and direction to close more of the gap to full fine-tuning.

## What's next

The next document answers What / Where / When / How / Why in detail. It covers why the low-rank assumption holds, how `A` and `B` are initialized so training starts from the unmodified model, how to choose rank and target modules, and how QLoRA and DoRA change the recipe.
