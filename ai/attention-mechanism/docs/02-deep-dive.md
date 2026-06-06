# Attention Mechanism — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Attention is a differentiable, content-based addressing operation. Given a query matrix `Q ∈ R^(n_q × d_k)`, a key matrix `K ∈ R^(n_k × d_k)`, and a value matrix `V ∈ R^(n_k × d_v)`, **scaled dot-product attention** computes:

```
Attention(Q, K, V) = softmax( Q K^T / sqrt(d_k) ) V
```

The output has shape `(n_q × d_v)`. Each output row is a convex combination of the rows of `V`, where the mixing coefficients are determined by the compatibility between the corresponding query row and every key row.

Tensor shapes walked through:

- `Q K^T` has shape `(n_q × n_k)` — one similarity score per (query, key) pair.
- The `softmax` is applied **row-wise**, turning each row of scores into a probability distribution over the `n_k` keys. Each row sums to 1.
- Multiplying that `(n_q × n_k)` weight matrix by `V` of shape `(n_k × d_v)` produces the final `(n_q × d_v)` output.

The `1 / sqrt(d_k)` factor is not cosmetic. If the components of `Q` and `K` are independent with mean 0 and variance 1, the dot product `q · k` has mean 0 and variance `d_k`. Without scaling, as `d_k` grows the logits passed into softmax grow in magnitude, softmax saturates into a near one-hot distribution, and `∂softmax/∂logit → 0` on every entry except the argmax. Training stalls. Dividing by `sqrt(d_k)` keeps the variance of the logits at roughly 1 regardless of head dimension, so gradients stay healthy.

### The core building blocks

- **Linear projections `W_Q, W_K, W_V`** — learned matrices that map input embeddings of dimension `d_model` into query, key, and value subspaces. These are the only learnable parameters in the attention op itself.
- **Score matrix `Q K^T`** — the raw compatibility table.
- **Softmax normaliser** — converts each row of scores into a probability distribution.
- **Value aggregator** — the second matmul that mixes value vectors according to the attention weights.
- **Mask** (optional) — an additive `(-inf, 0)` tensor applied before softmax to forbid certain attendances (causal, padding, document boundaries).
- **Output projection `W_O`** — for multi-head attention, mixes the concatenated head outputs back into `d_model`.

The original spec is Vaswani et al., *Attention Is All You Need* (NeurIPS 2017).

### How it relates to the broader landscape

Attention is the dominant member of the **content-based sequence mixing** family. Its siblings are **RNNs/LSTMs** (state-based sequential mixing), **CNNs** (fixed-kernel local mixing), and **state-space models** like S4/Mamba (linear recurrence with a learned dynamical system). Attention is the only one of these where the mixing weights are recomputed from the data at every layer — RNNs propagate state, CNNs use fixed filters, SSMs use a learned but input-independent kernel (selective SSMs add input dependence to recover some of attention's flexibility).

## Where

### Where it runs / lives in the stack

Attention is a layer **inside a transformer block**, on the application side of the ML stack. A standard pre-norm transformer block is:

```
x → LayerNorm → Attention → +residual → LayerNorm → FFN → +residual
```

It runs on accelerators (GPU/TPU) because the two matmuls dominate the cost and map cleanly onto tensor cores. In production inference servers it sits behind a tokenizer, an embedding lookup, and a KV-cache manager.

Three architectural placements matter:

- **Encoder self-attention** (BERT, ViT): bidirectional, no mask. Every token attends to every token.
- **Decoder self-attention** (GPT, Llama): causal mask, position `i` cannot attend to positions `> i`.
- **Cross-attention** (T5, original transformer, vision-language models): `Q` comes from the decoder, `K` and `V` come from the encoder output. The decoder uses this to "read" the source sequence.

### Where you typically encounter it

- LLMs: GPT-4/5, Claude, Llama 3/4, DeepSeek-V3, Mistral.
- Encoder models: BERT, RoBERTa, ModernBERT for retrieval and classification.
- Vision: ViT, DINOv2, SAM.
- Speech: Whisper, Conformer (hybrid CNN + attention).
- Multimodal: CLIP cross-attention, Gemini and Claude's vision pathways.

### Ecosystem and tooling

- **Kernels**: FlashAttention-2 / FlashAttention-3 (IO-aware exact attention on Hopper/Blackwell), xFormers, PyTorch's `scaled_dot_product_attention` (which dispatches to a fused kernel automatically as of 2.0+).
- **Inference KV-cache management**: vLLM (PagedAttention), TensorRT-LLM, SGLang.
- **Long-context variants**: Longformer (sliding window), BigBird (sparse), Linformer (low-rank), Performer (random features), Mamba/Mamba-2 (state-space hybrid).
- **Position injection**: sinusoidal (original), learned absolute (GPT-2/BERT), RoPE (Llama, GPT-NeoX, Qwen), ALiBi (Bloom, MPT).

## When

### When the topic emerged and why

Attention was introduced by Bahdanau et al. (2014) as an add-on to RNN encoder-decoder translation models. The bottleneck it addressed was concrete: seq2seq systems were forced to compress an arbitrary-length source sentence into a single fixed-size context vector, and BLEU scores on sentences over ~30 words collapsed. The 2017 transformer dropped the RNN entirely and showed attention alone was enough. By 2018 (BERT, GPT-1) attention-based models had taken over NLP; by 2020 (ViT) they took vision; by 2022 (Whisper, AlphaFold) they had spread across modalities.

### When to use it in a project

Reach for attention when:

- Token-level interactions over a sequence carry the task signal (translation, summarisation, QA, code, music).
- You need **parallel** training across the sequence dimension — attention computes all positions in two matmuls; an RNN cannot.
- The relevant context is **content-defined**, not position-defined (anaphora, function-call resolution, retrieval).
- You can afford `O(n^2 · d)` time and `O(n^2)` activation memory at the chosen sequence length, or you are willing to drop in FlashAttention / sliding-window variants.

### When NOT to use it

Avoid it when:

- Sequences are extremely long (100k+) and you cannot tolerate the quadratic cost — consider sliding-window attention, SSMs (Mamba-2), or hybrid stacks.
- The task is strictly local (small image patches, fixed-size signals) — a CNN is cheaper and inductively biased toward the right answer.
- Training data is tiny — attention layers are over-parameterised and overfit without regularisation or pretraining.
- Latency budget is sub-millisecond on CPU for short inputs — a small GRU or even logistic regression may dominate on wall-clock.

## How

### How it works under the hood

For a single self-attention layer with input `X ∈ R^(n × d_model)`:

1. **Project**: compute `Q = X W_Q`, `K = X W_K`, `V = X W_V`. Each `W_*` has shape `(d_model × d_model)` for multi-head attention, then is reshaped into `h` heads of dimension `d_k = d_model / h`.
2. **Reshape**: `Q, K, V` become `(batch, h, n, d_k)`.
3. **Score**: `S = Q K^T / sqrt(d_k)` of shape `(batch, h, n, n)`.
4. **Mask** (decoder only): add a strictly upper-triangular `-inf` matrix to `S` so position `i` cannot see `j > i`.
5. **Normalise**: `A = softmax(S, dim=-1)`.
6. **Aggregate**: `O = A V` of shape `(batch, h, n, d_k)`.
7. **Merge heads**: concatenate along the head axis to get `(batch, n, d_model)`.
8. **Output projection**: `Y = O W_O`.

Minimal PyTorch-flavoured pseudocode for the core op (single head, no batch dim for clarity):

```python
import torch
import torch.nn.functional as F

def attention(Q, K, V, mask=None):
    # Q: (n_q, d_k), K: (n_k, d_k), V: (n_k, d_v)
    d_k = Q.size(-1)
    scores = Q @ K.transpose(-2, -1) / d_k ** 0.5    # (n_q, n_k)
    if mask is not None:
        scores = scores.masked_fill(mask == 0, float("-inf"))
    weights = F.softmax(scores, dim=-1)              # (n_q, n_k)
    return weights @ V                               # (n_q, d_v)
```

Multi-head attention runs `h` such ops in parallel on slices of the projected tensors. The point of multiple heads is not capacity but **specialisation**: each head learns a different relation (one might track syntactic dependency, another coreference, another positional offset). GPT-3 uses `h=96, d_k=128`; Llama-3-8B uses `h=32` query heads with `d_k=128` and 8 KV heads (GQA).

**Positional encoding** is the companion mechanism. The attention op is permutation-equivariant: shuffling the rows of `X` shuffles the rows of the output identically. Word order is lost. To put it back, either add sinusoidal/learned vectors to the input embeddings (original transformer), or — more common in 2025 — apply **Rotary Position Embedding (RoPE)**, which rotates `Q` and `K` in 2D subspaces by an angle proportional to position. RoPE makes the dot product `q_i · k_j` a function of `i - j`, baking relative position into the score. ALiBi instead adds a linear penalty `-m · |i - j|` directly to the attention scores.

**Causal masking** is what makes a decoder autoregressive. The mask `M[i,j] = 0 if j <= i else -inf` is added to `S` before softmax. This guarantees that during training, predicting token `i+1` cannot peek at it — and means the same forward pass produces `n` parallel next-token predictions, one per position. At inference, this same masking justifies the **KV-cache**: once `K` and `V` for past tokens are computed, they never change, so they are stored and only the new token's `Q` runs against the growing `K, V` tensors. KV-cache size scales as `2 · n_layers · n_kv_heads · d_k · seq_len · batch · dtype_bytes` and is usually the dominant memory cost during generation, which is why GQA (grouped-query attention) and MQA (multi-query attention) exist — Llama-2-70B uses 8 KV groups for 64 query heads, shrinking the cache 8x.

### Key trade-offs

| Design choice | What you gain | What you give up |
|---|---|---|
| Full self-attention | Every token sees every other token; maximum expressiveness | `O(n^2)` time and memory |
| Multi-head split | Specialised relations per head, better optimisation | Each head sees only `d_model / h` dimensions |
| Causal mask | Enables autoregressive training in one pass | Half the score matrix is wasted compute |
| GQA / MQA | KV-cache shrinks, decode throughput rises | Small quality drop vs full MHA |
| Sliding-window attention | Linear cost in `n` | Loses long-range dependencies unless stacked deep |
| RoPE positions | Relative-position semantics, decent length extrapolation | Couples position to head dimension geometry |
| FlashAttention kernel | 2–4x faster, much lower HBM traffic, exact result | Implementation locked to specific GPU architectures |

### Common failure modes

- **Softmax saturation when scaling is wrong** — forgetting `/ sqrt(d_k)` or applying it in the wrong place collapses gradients on day one.
- **Attention sink on token 0** — in long-context models, the BOS token absorbs disproportionate mass; breaks naive cache eviction strategies.
- **OOM on long context** — `n^2` activation memory blows past HBM around 8k–32k tokens for naive implementations; FlashAttention is the fix.
- **KV-cache memory wall during serving** — long conversations eat tens of GB of cache per request; PagedAttention (vLLM) addresses this.
- **Position extrapolation failure** — a model trained at 4k tokens with sinusoidal or learned absolute positions degrades sharply past training length; RoPE with NTK-aware scaling or YaRN extends usable length.
- **Numerical overflow in fp16** — large logits before scaling overflow; mixed precision implementations subtract the row max inside softmax for stability.
- **Causal mask leak in custom kernels** — off-by-one in the mask leaks one future token, which is invisible in loss curves but catastrophic for autoregressive sampling.

## Why

### Why it exists

Attention exists to solve the **information bottleneck** problem in sequence modelling. RNNs had to squeeze a variable-length past into a fixed-size vector and update it serially, which is both lossy and parallel-unfriendly. Attention removes the bottleneck by letting every output position pull directly from any input position, and removes the serial dependency by computing all positions in two matrix multiplications that map cleanly onto GPU tensor cores. The same operation simultaneously solved a *modelling* problem (long-range dependencies) and a *systems* problem (parallel training), which is why it took over so completely.

### Why it looks the way it does

The non-obvious design choice is **content-based weighting via dot products**, not learned per-position weights. A per-position weight matrix would have size `O(n^2)` parameters, would not generalise across sequence lengths, and would not be permutation-equivariant. Dot products between projected queries and keys give you a **learned similarity function with zero per-position parameters** — the same `W_Q, W_K` work at any sequence length, on any sequence. The softmax is the smallest differentiable surrogate for "argmax over keys" that preserves gradient flow to all positions. The scaling by `sqrt(d_k)` is the smallest correction that keeps softmax in its useful regime as `d_k` grows.

The obvious alternative — additive attention from Bahdanau et al. (concatenate `q` and `k`, push through a small MLP, scalar output) — is roughly equivalent in quality at small `d_k` but cannot be expressed as a single matmul. Dot-product attention won because two big matmuls are the most GPU-friendly primitive in existence.

### Why it matters now

In 2026 attention is still the load-bearing primitive in every frontier LLM, every state-of-the-art vision encoder, and most multimodal systems. The interesting movement is **not** away from attention — pure Mamba and other SSMs underperform on associative recall and few-shot tasks — but toward **hybrids** (Jamba, Zamba, Granite-4) that interleave attention layers with SSM or sliding-window layers to keep quadratic cost bounded while preserving attention's recall behaviour. Understanding the exact mechanics of attention is therefore the prerequisite for understanding everything currently shipping: GQA, MLA (Multi-head Latent Attention in DeepSeek-V3), PagedAttention, FlashAttention-3, and the entire long-context optimisation literature.

## Open questions / things to verify in practice

- How much does dropping `/ sqrt(d_k)` actually hurt? Train two tiny models side by side and watch the loss curves.
- At what sequence length does naive PyTorch attention OOM on your GPU vs `F.scaled_dot_product_attention`? Measure it.
- How much KV-cache memory does a 7B model with GQA actually use at 32k context, batch 8? Compute it from `2 · L · n_kv · d_k · seq · 2` bytes (fp16) and verify against `nvidia-smi`.
- Do attention heads actually specialise, or is that folklore? Probe a small pretrained model with attention-pattern visualisation.
- How far can RoPE extrapolate beyond training length before quality drops, with and without YaRN scaling?
- For your real workload, what is the FLOPs split between attention and the FFN? On most modern LLMs the FFN dominates — verify before optimising attention.

Sources:
- [Scaled Dot-Product Attention Explained: Why We Divide by sqrt(dk)](https://www.aryanupadhyay.com/post/scaled-dot-product-attention-explained-why-we-divide-by-d%E2%82%96-in-transformers)
- [FlashAttention: Fast and Memory-Efficient Exact Attention with IO-Awareness (arXiv 2205.14135)](https://arxiv.org/abs/2205.14135)
- [Grouped-Query Attention (Sebastian Raschka)](https://sebastianraschka.com/llms-from-scratch/ch04/04_gqa/)
- [Positional Embeddings in Transformer Models (ICLR Blogposts 2025)](https://iclr-blogposts.github.io/2025/blog/positional-embedding/)
- [End of Transformers? Attention + State-Space Hybrids in 2025](https://www.askaibrain.com/en/posts/end-of-transformers-hybrids-attention-state-space-2025/)
