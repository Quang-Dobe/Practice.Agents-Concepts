# Attention Mechanism — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical LLM serving stack, attention is not the line of code you write — it is the line of code you call. You import `torch.nn.functional.scaled_dot_product_attention`, or you launch `vllm serve meta-llama/Llama-3.1-8B-Instruct`, and the entire attention pipeline (kernel choice, masking, KV-cache layout) is decided for you by libraries written by people who have spent years on a single softmax.

You meet attention as an *operational* concern in three places. **Training**: your fine-tuning job OOMs at 8k context, or your loss curves look great but generations are gibberish because the causal mask was off by one. **Inference**: your p99 latency doubles when one user pastes a 50k-token document, because KV-cache memory just evicted every other request. **Model selection**: you have to choose between a Llama-3 70B with grouped-query attention and a Mistral with sliding-window attention, and the right answer depends on whether your workload is many short prompts or few long ones.

The day-to-day skill is not deriving softmax. It is reading a model card, recognising which attention variant it uses, and predicting how that variant will behave under your specific traffic shape.

## Best practices

### 1. Use a fused kernel — never hand-write the matmul-softmax-matmul
**Do:** Call `F.scaled_dot_product_attention` (PyTorch 2.0+) or FlashAttention-2/3 directly. SDPA auto-dispatches to FlashAttention, memory-efficient attention, or cuDNN based on shapes and dtype.
**Why:** The naive `softmax(Q @ K.T / sqrt(d)) @ V` materialises an `(n, n)` activation that hits HBM twice. FlashAttention tiles the computation and keeps it in SRAM, cutting HBM traffic 5-10x and unlocking 2-4x wall-clock speedups, especially past 2k tokens.
**Avoid:** Reaching for a "from scratch" attention class in production code. It is correct, slow, and OOMs at exactly the context length your customers will use.

### 2. Pick the right kernel for the right shape
**Do:** For training and prefill, FlashAttention-2/3 (or SDPA). For decode (single-token query against a long KV cache), use PagedAttention via vLLM or TensorRT-LLM — they specialise on the `n_q=1` case.
**Why:** FlashAttention is tuned for `n_q ≈ n_k`. During autoregressive decode `n_q = 1` and `n_k = seq_len`, which makes a generic kernel memory-bound on KV reads. PagedAttention's block-table indirection wins here.
**Avoid:** Using a training-time kernel for decode and being puzzled that your tokens/sec is 5x worse than vLLM on the same hardware.

### 3. Treat the KV cache as your real memory budget
**Do:** Compute `2 · n_layers · n_kv_heads · d_head · seq_len · batch · dtype_bytes` before deploying. For a 70B Llama at 32k context, batch 8, fp16 with 8 KV groups, that is already tens of GB — often more than the weights at long context.
**Why:** Decode is memory-bound on KV reads. KV cache size — not parameter count — determines max batch and max context on a given GPU.
**Avoid:** Sizing GPUs for model weights and discovering at peak load that one 100k-token conversation evicts every other request.

### 4. Use grouped-query attention (GQA) or multi-query attention (MQA) for serving
**Do:** Pick base models that ship with GQA (Llama-3, Qwen-2.5, Mistral) or MQA. They share KV heads across query heads, shrinking the KV cache 4-8x at minor quality cost.
**Why:** Full multi-head attention with 64 KV heads makes long-context serving uneconomical. GQA with 8 KV groups is the de facto standard for the same reason.
**Avoid:** Fine-tuning a full-MHA model for production serving when a GQA variant of similar quality exists.

### 5. Match positional encoding between training and inference
**Do:** If the model was trained with RoPE at base frequency 10000 and 8k context, and you want to serve at 32k, apply NTK-aware scaling or YaRN — and verify quality on a held-out set at the extended length.
**Why:** Position encoding is the silent failure mode of long context. A model can produce fluent-looking but factually drifting output past its training length without any loss spike to warn you.
**Avoid:** Cranking `max_position_embeddings` in the config and calling it done. The model has not seen those rotations during training.

### 6. Get the masks right — and test them
**Do:** Write an explicit unit test that runs the same prompt twice with different right-padding and asserts identical logits at non-pad positions. Do the same for causal: predicting token `i` from prefix `1..i` should be bit-identical to predicting it from the full sequence.
**Why:** A broken padding mask injects NaN through softmax on a row of all `-inf`. A broken causal mask leaks one future token, which is invisible in loss curves but catastrophic for sampling quality.
**Avoid:** Trusting that your custom mask is correct because "the loss went down." Both of these bugs lower loss while breaking generation.

### 7. Watch for fp16/bf16 softmax overflow
**Do:** Use bf16 over fp16 where the hardware supports it (Ampere+, TPU v3+). Rely on kernels that subtract the row-max inside softmax — FlashAttention and SDPA do this for you.
**Why:** Raw logits at `d_k=128` can hit magnitudes that overflow fp16 (`>65504`). The result is NaN that propagates the rest of training.
**Avoid:** Hand-rolled `softmax(scores)` in fp16 without the row-max subtraction trick.

### 8. Place dropout after the softmax, not after the output projection
**Do:** Apply attention dropout to the softmax output (the weights), as in the original transformer. Use it sparingly — modern LLMs often set it to 0.
**Why:** Dropout on attention weights randomly rebalances the mixing, which acts as a regulariser on which positions get attended to. Dropping the output projection post-hoc is a different, weaker effect.
**Avoid:** Putting dropout in three different places "for safety." It usually hurts, especially during fine-tuning.

### 9. Use continuous batching for serving, not static batches
**Do:** Run vLLM, SGLang, or TGI with continuous (iteration-level) batching enabled. New requests join the batch at the next decode step; finished ones free their KV blocks immediately.
**Why:** Static batching idles the GPU while the longest generation in the batch finishes. Continuous batching plus PagedAttention is what gets vLLM to 3-5x the throughput of a naive HuggingFace generate loop.
**Avoid:** Building a "batch of 8 requests, wait, return" service. You will be CPU-idle on long generations and GPU-idle on short ones.

### 10. Reach for efficient/linear variants only when the quadratic cost actually hurts
**Do:** Default to full attention. Move to sliding-window (Mistral), grouped local + global (Longformer/BigBird), low-rank (Linformer), kernelised (Performer), or hybrid SSM (Jamba, Zamba) when profiling shows attention dominates your runtime *and* your sequences are long enough that quadratic cost is the bottleneck.
**Why:** On most modern LLMs the FFN dominates FLOPs at the sequence lengths people actually use. Optimising attention you weren't bottlenecked on costs quality for no throughput.
**Avoid:** Adopting Performer because a blog post said it was linear. The constant factors and quality regressions usually swamp the asymptotic win below 16k tokens.

### 11. Debug with attention maps, but don't over-interpret them
**Do:** When a model misbehaves, visualise the `(n, n)` weight matrix per head on a representative input. Look for the canonical patterns: diagonal (local), vertical stripe on position 0 (attention sink), block-diagonal (document structure).
**Why:** Many production bugs (wrong tokenizer, doubled BOS, lost separator token) show up immediately as a weird attention map before they show up in eval metrics.
**Avoid:** Concluding "head 7 is the coreference head" from one example. Head specialisation is real but noisy; claims need probing across many inputs.

### 12. Profile attention vs FFN before optimising
**Do:** Use `torch.profiler` or NVIDIA Nsight to measure where time is actually spent. On a Llama-3-8B at 4k context, attention is often <30% of forward time; the FFN and the QKV projections dominate.
**Why:** "Optimise attention" is the default instinct because attention is the famous part. The cost picture rarely matches the fame.
**Avoid:** Spending a week swapping kernels for a 5% speedup on a layer that was 20% of runtime.

## Anti-patterns to recognise

- **The hand-rolled attention class:** A `class Attention(nn.Module)` with explicit `Q @ K.T / math.sqrt(d)`. Looks educational, OOMs at 8k context, runs 3x slower than SDPA. Replace with `F.scaled_dot_product_attention`.
- **Per-request KV cache allocation:** Allocating a fresh contiguous `(max_seq, n_kv, d_head)` tensor per request. Wastes 60-80% of GPU memory to fragmentation and over-allocation. Use PagedAttention via vLLM.
- **Padding-as-mask confusion:** Building the attention mask from `input_ids != pad_token_id` but forgetting the causal triangle, or vice versa. The two must be combined before softmax. The fix is one composed mask built once per batch.
- **RoPE without scaling at extended context:** Loading a 4k-trained checkpoint and setting `max_position_embeddings=32768` with no theta or NTK adjustment. Generations stay fluent and become quietly wrong. Use YaRN, NTK-aware scaling, or fine-tune at the target length.
- **Dropping attention dropout into a pretrained model:** Adding non-zero `attn_dropout` during fine-tuning of a model pretrained with zero. Shifts the activation statistics every layer sees and destabilises training. Match the pretraining config.
- **Trusting loss curves to catch mask bugs:** A causal mask that leaks one future token *lowers* loss. The bug surfaces only at autoregressive sampling. Always add a "same prompt, with and without future tokens visible" parity test.
- **Optimising attention when the FFN is the bottleneck:** Swapping kernels and quantising QKV while the gated MLP is 60% of forward time. Profile before optimising.
- **Treating "linear attention" as a free win:** Performer, Linformer, and friends trade exactness for asymptotics. Below ~16k tokens the constants lose; quality regressions on recall-heavy tasks (needle-in-a-haystack, multi-hop QA) are well-documented.

## Real-world usage patterns

**LLM serving at a SaaS company.** A team serves a 70B chat model behind an API with mixed-length traffic (median 500 tokens, p99 32k). They run vLLM with PagedAttention and continuous batching on H100s. The non-obvious lesson: their main quality regression came not from the kernel but from a tokenizer mismatch between training and serving that broke RoPE position alignment after a system-prompt update.

**Fine-tuning a base model for a vertical.** A startup LoRA-fine-tunes Llama-3-8B on legal documents averaging 12k tokens. They train with FlashAttention-2 in bf16, document-pack with cross-document attention masked off, and use sample-packing to keep GPU utilisation high. The non-obvious lesson: document-boundary masks are easy to get wrong, and the symptom is the model "leaking" content from one document into another's answer at eval time.

**Long-context retrieval-augmented system.** A team builds a 200k-context QA system over Claude or a long-context open model. They discover the "lost-in-the-middle" pattern — answers in the middle 50% of the context are retrieved poorly. The non-obvious lesson: this is partly an attention-sink artefact (BOS and end tokens absorb mass), and reordering retrieved chunks to put the most important ones at the start or end measurably improves accuracy without changing the model.

**On-device vision encoder.** A mobile app runs a small ViT for image classification on-device. They use INT8 quantisation and a sliding-window attention variant to keep the activation footprint under 50 MB. The non-obvious lesson: post-training quantisation of the softmax inputs (logits) is far more fragile than quantising the QKV projections; the team kept softmax in fp16 and quantised everything else.

## Operational checklist

- KV-cache memory budget computed and verified against `nvidia-smi` at expected max batch and max context?
- Attention kernel chosen explicitly (SDPA / FlashAttention / PagedAttention), and benchmarked against the alternative on representative shapes?
- Causal-mask parity test in CI (same prompt, with and without future tokens visible, identical logits)?
- Padding-mask test in CI (right-padding to different lengths produces identical logits at real positions)?
- Position encoding strategy matches between checkpoint and serving config (RoPE base, scaling factor, max length)?
- Continuous batching enabled in the serving runtime, with KV-cache eviction policy documented?
- p99 latency measured at realistic long-context tail, not just median?
- Attention-vs-FFN time split profiled at least once, so the next optimisation target is the actual bottleneck?
- Eval suite includes a long-context probe (needle-in-a-haystack or equivalent) at the deployed max length?
- New engineer can answer on day one: "what attention variant does our model use, and what is our KV-cache headroom at peak load?"

## How this topic typically evolves in a codebase

Most projects start with `transformers.AutoModelForCausalLM` and a `model.generate()` loop. That works up to roughly one GPU and a few requests per second. The first migration is to vLLM or TGI, driven by the realisation that static batching is leaving 70% of the GPU on the floor. At that point the team learns the words "PagedAttention" and "KV cache" the hard way.

The second migration tends to be context length. The product wants 128k, the model was trained at 8k, and someone has to evaluate RoPE scaling techniques and quality at extended length. This is where teams discover the difference between "the model accepts 128k tokens" and "the model is useful at 128k tokens." The fix is usually either continued pretraining at the target length or accepting a shorter effective context with a smarter retrieval layer in front.

The third evolution, if the workload grows, is architectural: moving to a model with GQA/MQA if it doesn't already have it, or to a hybrid attention+SSM model (Jamba, Granite-4) to cap quadratic cost. By this point attention is no longer a thing the team writes — it is a thing the team selects, configures, and monitors. The painful realisation along the way is that the dominant cost of attention in production is not FLOPs but memory bandwidth on the KV cache, and that fact reshapes every downstream decision.

## Further reading

- [FlashAttention-2 paper (Dao, 2023)](https://arxiv.org/abs/2307.08691) — the kernel that made long-context training economical; read the IO-complexity analysis.
- [vLLM / PagedAttention paper (SOSP 2023)](https://arxiv.org/abs/2309.06180) — the OS-virtual-memory analogy applied to KV cache; the foundation of modern LLM serving.
- [GQA paper (Ainslie et al., 2023)](https://arxiv.org/abs/2305.13245) — why every modern open model uses grouped-query attention, with the quality/cost curves.
- [YaRN: Efficient Context Window Extension of LLMs (Peng et al., 2023)](https://arxiv.org/abs/2309.00071) — the canonical reference for extending RoPE-trained models past their training length.
- [Lost in the Middle (Liu et al., 2023)](https://arxiv.org/abs/2307.03172) — empirical study of long-context attention failure modes; required reading before shipping a RAG system.
- [PyTorch `scaled_dot_product_attention` docs](https://pytorch.org/docs/stable/generated/torch.nn.functional.scaled_dot_product_attention.html) — the one API you will actually call; understand its dispatch rules and when each backend is selected.
