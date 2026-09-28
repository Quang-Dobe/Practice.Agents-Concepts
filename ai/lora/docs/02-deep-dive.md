# LoRA — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

LoRA (Low-Rank Adaptation, [Hu et al., 2021](https://arxiv.org/abs/2106.09685)) is a **reparameterization-based parameter-efficient fine-tuning (PEFT)** method. For a frozen pretrained weight matrix `W₀ ∈ ℝ^(d×k)`, LoRA constrains the fine-tuning update to a rank-`r` factorization `ΔW = B·A`, with `B ∈ ℝ^(d×r)`, `A ∈ ℝ^(r×k)` and `r ≪ min(d, k)`. The forward pass of an adapted linear layer becomes:

```text
h = W₀·x + (α / r) · B·A·x
```

Only `A` and `B` receive gradients. `W₀` has no gradient and no optimizer state. Trainable parameters per adapted matrix fall from `d·k` to `r·(d + k)`.

### The core building blocks

- **Frozen base weights (`W₀`)**: the pretrained matrices. They are stored in bf16/fp16 (plain LoRA) or 4-bit (QLoRA) and are never updated.
- **Down-projection `A` (r × k)**: maps the layer input into an `r`-dimensional subspace. Initialized randomly: Gaussian in the paper, Kaiming-uniform in Hugging Face `peft`.
- **Up-projection `B` (d × r)**: maps back to the output dimension. **Initialized to zero**, so `ΔW = 0` at step 0 and the adapted model starts out computing exactly what the base model computes.
- **Scaling factor `α / r`**: multiplies the delta. The paper holds `α` fixed while you vary `r`, so you don't need to re-tune the learning rate for every rank. rsLoRA ([Kalajdzievski, 2023](https://arxiv.org/abs/2312.03732)) replaces it with `α / √r`, which keeps training stable at high ranks.
- **Target modules**: the set of linear layers that get adapters. The paper adapted only `W_q` and `W_v`. Current practice adapts every linear layer (attention and MLP).
- **Adapter artifact**: a file (in `peft`, `adapter_model.safetensors` + `adapter_config.json`) that holds only the `A`/`B` tensors plus metadata naming the base model.
- **Merge operation**: `W' = W₀ + (α/r)·B·A`. This is a one-time, lossless (in full precision) fold that removes all inference overhead.

### How it relates to the broader landscape

LoRA belongs to the PEFT family, which has three branches ([Lialin et al., 2023](https://arxiv.org/abs/2303.15647)):

- **Additive** methods insert new modules or tokens. Examples are adapter layers ([Houlsby et al., 2019](https://arxiv.org/abs/1902.00751)), prefix tuning and prompt tuning.
- **Selective** methods train a subset of the existing weights. BitFit, for example, trains only the biases.
- **Reparameterization** methods, LoRA among them, express the update in a compact form.

LoRA's distinguishing property is that the update lives *in parallel* with an existing linear layer and can be merged back into it. It is the only mainstream PEFT method that can have zero inference cost. Its descendants (QLoRA, DoRA, LoRA+, PiSSA, rsLoRA) keep that structure and change the precision, the initialization, the scaling or the decomposition.

## Where

### Where it runs / lives in the stack

LoRA lives in the **model-training layer** and, when adapters are not merged, in the **inference-serving layer**. At training time it is a module wrapper around `nn.Linear` (or `Conv2d` / `Embedding`) inside the framework graph. At serving time an inference engine either loads merged weights, which look like an ordinary checkpoint, or keeps one base model resident and applies per-request adapter deltas with batched kernels.

### Where you typically encounter it

- **Open-weights LLM customization**: fine-tuned Llama, Mistral, Qwen and Gemma variants on the Hugging Face Hub, most of them published as LoRA adapters.
- **Diffusion models**: Stable Diffusion / SDXL / Flux "LoRAs" for styles and characters, distributed as small `.safetensors` files.
- **Multi-tenant LLM serving**: one base model serving many customer adapters (vLLM, TGI/LoRAX, NVIDIA NIM, Amazon SageMaker, Anyscale).
- **Managed fine-tuning APIs**: several hosted fine-tuning services train adapter-style updates behind the API. The vendors generally don't document the exact method.
- **RL post-training**: Thinking Machines' [LoRA Without Regret](https://thinkingmachines.ai/blog/lora/) (2025) found that LoRA matches full fine-tuning on policy-gradient RL even at rank 1.

### Ecosystem and tooling

- **For defining adapters**: Hugging Face [`peft`](https://huggingface.co/docs/peft) (`LoraConfig`, `get_peft_model`), Microsoft's original `loralib`.
- **For training loops**: Hugging Face `transformers` Trainer and `trl` (SFT, DPO, GRPO trainers that accept a `peft_config`), Axolotl, Unsloth (fused kernels, lower memory), LLaMA-Factory, torchtune.
- **For quantized bases**: `bitsandbytes` (NF4 for QLoRA), plus GPTQ/AWQ bases in some stacks.
- **For serving many adapters**: vLLM (`--enable-lora`, `--max-loras`, `--max-lora-rank`, with Punica-derived SGMV kernels), [S-LoRA](https://arxiv.org/abs/2311.03285), LoRAX, TensorRT-LLM.
- **For diffusion**: `diffusers` (`load_lora_weights`), kohya-ss scripts, ComfyUI / Automatic1111 loaders.

## When

### When the topic emerged and why

By 2020 GPT-3 (175B) had made full fine-tuning impractical for most teams. Each task needed a 350 GB checkpoint and about 1.2 TB of training VRAM. The existing PEFT options each had a cost:

- **Adapter layers** added sequential compute. The LoRA paper measured +20.7% to +30.3% latency at batch size 1 on GPT-2 Medium.
- **Prefix tuning** was hard to optimize and consumed context length.

Earlier research had shown that fine-tuning has a low *intrinsic dimension* ([Li et al., 2018](https://arxiv.org/abs/1804.08838); [Aghajanyan et al., 2020](https://arxiv.org/abs/2012.13255)). Hu et al. at Microsoft turned that observation into a practical method in June 2021. On GPT-3 it:

- cut training VRAM from 1.2 TB to 350 GB
- shrank checkpoints from 350 GB to 35 MB (`r=4`, `W_q`/`W_v` only)
- trained 25% faster than full fine-tuning

The method's adoption rose sharply in 2023. [QLoRA](https://arxiv.org/abs/2305.14314) (Dettmers et al., May 2023) made it possible to fine-tune a 65B model on a single 48 GB GPU, and the open-weights Llama models gave people something to fine-tune.

### When to use it in a project

Reach for it when:

- You own the weights and need behavioral change (format, tone, tool-call schema, domain style, classification head) that prompting can't reliably produce.
- Your dataset is small to medium (hundreds to roughly 100K examples), the size LoRA Without Regret calls the "low-regret" regime.
- You need N variants of one base model and can't store or serve N full copies.
- Your GPU budget is a single 24–80 GB card rather than a multi-node cluster.
- You are doing RL/preference post-training (DPO, GRPO), where the reward signal carries little information per episode and even a very low rank is enough.

### When NOT to use it

Avoid it when:

- The goal is **knowledge injection at scale**, such as continued pretraining on billions of tokens or a new language. [Biderman et al., 2024](https://arxiv.org/abs/2405.09673) showed that LoRA "learns less" in these regimes. Full fine-tuning learns perturbations of 10–100× higher rank than typical LoRA configs.
- The facts change weekly. RAG is cheaper to update and can be audited.
- You have no weight access, only a closed API.
- Latency is critical, you must keep adapters unmerged (multi-tenant), and your engine lacks batched LoRA kernels. The per-request overhead becomes measurable.

## How

### How it works under the hood

**Training lifecycle**

1. **Load the base** in bf16, or in 4-bit NF4 for QLoRA. Set `requires_grad=False` on every parameter.
2. **Wrap target modules.** `peft` replaces each matching `nn.Linear` with a `lora.Linear` that holds the original layer plus the `lora_A` and `lora_B` sub-modules and a dropout. Matching is by module-name suffix (`q_proj`, `down_proj`, …), or `"all-linear"`.
3. **Initialize.** `A` is random and `B = 0`, so the model's output is bit-identical to the base at step 0.
4. **Forward.** Compute `W₀x` as normal, compute `B(A(dropout(x)))`, scale it by `α/r` and add. The extra FLOPs are `2·r·(d+k)` per token per layer, a small fraction of `2·d·k`.
5. **Backward.** Gradients still flow *through* `W₀` to earlier layers, so activation memory is similar to full fine-tuning. Only `A` and `B` accumulate weight gradients.
6. **Optimizer step.** AdamW keeps two fp32 moments per *trainable* parameter only. This is where most of the memory saving comes from.
7. **Save** only the adapter tensors.

**Memory arithmetic (7B model, rule of thumb)**: full fine-tuning with mixed-precision AdamW needs about 16 bytes per parameter (weights, gradients, fp32 master copy, two moments), so about 112 GB before activations. LoRA on a bf16 base needs about 14 GB for the weights plus a few hundred MB for the adapter and its optimizer state. QLoRA brings the base down to about 4 GB. In every case activations and KV/sequence length come on top.

**Parameter count example**: a Llama-2-7B-shaped model (32 layers, hidden 4096, MLP 11008) with `r=16` on all seven linear projections has 16·(4·8192 + 3·15104) = about 1.25M parameters per layer, or about 40M total (about 0.6% of 6.7B).

**QLoRA additions** ([Dettmers et al., 2023](https://arxiv.org/abs/2305.14314)):

- **NF4**, a 4-bit data type whose quantization levels are placed for normally distributed weights.
- **Double quantization**, which also quantizes the per-block scaling constants.
- **Paged optimizers**, which spill optimizer state to CPU RAM when memory spikes.

Compute runs in bf16: each NF4 block is dequantized on the fly, and gradients flow into the bf16 LoRA weights.

**DoRA** ([Liu et al., ICML 2024](https://arxiv.org/abs/2402.09353)) rewrites each weight as `m · (W₀ + BA) / ‖W₀ + BA‖_c`, where `m` is a learned per-column magnitude vector and LoRA updates only the direction. Its update patterns look more like full fine-tuning's, at some extra training compute.

**Inference paths**

```text
Merged:     x ──► [W₀ + (α/r)BA] ──► h            (zero overhead, one model per adapter)

Unmerged:   x ──► W₀ ────────────┐
            └──► A ──► B ──► ×α/r ┴─(+)──► h      (one base, many adapters, small extra matmuls)
```

In multi-adapter serving, a batch mixes requests that target different adapters. Engines like vLLM gather each request's `A`/`B` with segmented kernels (SGMV/BGMV from Punica) and keep hot adapters in an LRU cache in GPU memory, with CPU memory as a fallback tier.

### Key trade-offs

| Design choice | You gain | You give up |
|---|---|---|
| Low rank `r` (4–16) vs high (64–256) | Fewer parameters, less overfitting, smaller files | Capacity. Large or knowledge-heavy datasets underfit at low rank |
| Attention-only vs all linear layers | Fewer parameters, matches the paper | Quality. LoRA Without Regret finds attention-only underperforms even at matched parameter count |
| Merge vs keep unmerged | Zero latency, standard checkpoint | Hot-swapping and multi-tenant batching; one full copy per adapter |
| QLoRA (4-bit base) vs bf16 base | About 4× less base memory | Slower steps (dequantization), a small quality risk, a lossy merge unless you re-merge into a bf16 base |
| LoRA vs full fine-tuning | Order-of-magnitude memory savings, less forgetting of base skills | Peak in-domain quality on large or continued-pretraining workloads |
| `α/r` vs rsLoRA `α/√r` | Paper-compatible defaults | High-rank stability (plain `α/r` shrinks the update as `r` grows) |
| DoRA vs LoRA | Closer to full fine-tuning at the same rank | Extra compute and memory in training, less mature kernel support in serving |

### Common failure modes

- **Trainable parameter count is zero or tiny.** Cause: the `target_modules` names don't match the architecture (e.g. `c_attn` in GPT-2 vs `q_proj` in Llama).
- **Loss barely moves.** Cause: the learning rate was copied from full fine-tuning. LoRA's optimal LR is about 10× higher (LoRA Without Regret), and a very small `α/r` shrinks it further.
- **Divergence or NaNs at high rank.** Cause: the `α` scaling interacts badly with high rank, or LoRA+ ratios are too aggressive. Use rsLoRA or lower the LR.
- **Good eval but garbage in production.** Cause: the chat template or tokenizer differs between training and serving, or the adapter is loaded onto a different base revision.
- **Quality drops after merging a QLoRA adapter.** Cause: the adapter was merged into a quantized base, or the merged weights were re-quantized. Merge into a bf16 base.
- **Serving engine rejects the adapter.** Cause: the adapter rank exceeds the engine's limit. vLLM's `--max-lora-rank` defaults to 16 in current docs.
- **New special tokens produce noise.** Cause: embeddings and `lm_head` were frozen. List them in `modules_to_save` or train the embedding rows.
- **Gradient checkpointing crashes or yields no gradients.** Cause: the frozen input embeddings produce tensors without `requires_grad`. Call `enable_input_require_grads()`.
- **The adapter memorizes the training set.** Cause: too few examples, too many epochs, or too high a rank for the data size.

## Why

### Why it exists

Fine-tuning cost scales with the parameter count. The information a downstream task adds does not: it is bounded by the dataset, which is tiny compared with pretraining. LoRA exploits that asymmetry. It spends memory, storage and optimizer compute on an `r`-dimensional subspace that roughly matches the task's information content. The principles at stake are **cost** (GPU memory is the binding constraint), **modularity** (a base/delta split resembles a base image plus layers), and **latency** (zero overhead once merged).

### Why it looks the way it does

- **Parallel rather than serial.** Adapter layers insert a bottleneck MLP *in series*, which adds depth and therefore latency that batching can't hide at batch size 1. A parallel low-rank branch on an existing linear map is also linear, so it can be algebraically folded into `W₀`. No serial design can offer that.
- **A factorization rather than a sparse mask or bias-only update.** BitFit (biases only) is cheaper but has little capacity. Sparse updates have irregular memory access patterns that GPUs handle poorly. A dense low-rank product is two small GEMMs, which GPUs execute efficiently.
- **`B = 0` initialization.** The model starts exactly at the pretrained function, so early training can't damage it. The asymmetry this creates (at step 0, `A` receives no gradient while `B = 0`) is what LoRA+ ([Hayou et al., 2024](https://arxiv.org/abs/2402.12354)) addresses by giving `B` a higher learning rate.
- **`α/r` scaling.** It keeps the effective update magnitude roughly constant as you sweep `r`, so hyperparameters transfer. rsLoRA later showed that `√r` is the scaling that stays stable as `r` grows.

### Why it matters now

In 2026 LoRA is the default fine-tuning primitive for open-weights models. It is changing rather than fading:

- RL post-training (GRPO-style reasoning tuning) is often done with LoRA, because rank-1 adapters absorb the reward signal.
- Serving stacks treat per-tenant adapters as a first-class feature.
- The research has moved from "does LoRA work?" to "when does it match full fine-tuning?". The answer: most post-training workloads, if you target all layers and use about 10× the full-fine-tuning learning rate.

The main open gap is large-scale knowledge acquisition, where full fine-tuning and continued pretraining still win.

## Open questions / things to verify in practice

- Does applying LoRA to `"all-linear"` layers at `r=16` beat `q_proj`/`v_proj` at `r=64` on your task, at matched parameter count?
- What is the learning-rate sweet spot relative to your full-fine-tuning baseline? Is it really about 10×?
- How large is the measured latency and throughput overhead of unmerged adapters in your serving engine at your real batch sizes, compared with a merged checkpoint?
- After QLoRA training, is there a measurable quality gap between merging into the bf16 base and running the 4-bit base plus the adapter?
- How much general capability (e.g. a held-out general benchmark) does your adapter cost, compared with a full fine-tune on the same data?
- Do your adapter's rank and target modules fit the limits of the serving engine you will deploy on (`--max-lora-rank`, supported module types)?
