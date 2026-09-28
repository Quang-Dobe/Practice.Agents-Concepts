# LoRA — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a product team running an open-weights LLM, LoRA is the thing between "the prompt is 3,000 tokens of instructions and still breaks the JSON schema 4% of the time" and "we need a training cluster". A team trains an adapter on a few thousand curated examples, ships a few-hundred-MB file, and loads it onto the base model the inference fleet already serves.

In a multi-tenant SaaS (support bots per customer, per-tenant classifiers, per-language SQL generators), LoRA is the unit of tenancy. One base model stays resident on the GPU, and each request names its adapter. The serving engine's adapter cache and rank limits become capacity-planning inputs, just like connection pools.

In ML platform and post-training work, LoRA runs inside the training pipeline itself: SFT, DPO and GRPO runs with `trl` plus a `peft_config`, evaluated and promoted like any other build artifact. In image generation, "a LoRA" is a style or character file that users swap at runtime.

## Best practices

### 1. Establish a prompt-only and a RAG baseline before training anything
**Do:** Measure the base model with your best prompt (and few-shot examples, and retrieval if the task is fact-heavy) on the same eval set you will use for the adapter.
**Why:** Half of "we need fine-tuning" requests are solved by a better prompt or retrieval. Without a baseline you cannot prove the adapter earns its operating cost: a training pipeline, a model registry, a serving config and a retraining cadence.
**Avoid:** Fine-tuning to teach facts that change weekly. The adapter goes stale and nobody can audit where an answer came from.

### 2. Target all linear layers, not just `q_proj`/`v_proj`
**Do:** Use `target_modules="all-linear"` (attention and MLP) as the default, then shrink `r` if you need fewer parameters.
**Why:** LoRA Without Regret found attention-only rank 256 underperforms MLP-only rank 128 at about the same parameter count. Raschka's experiments showed a clear gain from enabling all layers for about 2.4 GB more memory on a 7B model.
**Avoid:** Copying the 2021 paper's `q_proj`/`v_proj` config from an old tutorial and then raising the rank to compensate.

### 3. Re-tune the learning rate. Don't copy it from full fine-tuning
**Do:** Start at about 10x your full-fine-tuning LR (e.g. `2e-4` instead of `2e-5` for a 7B SFT run) and sweep 3–4 values around it. Short runs of about 100 steps may want closer to 15x.
**Why:** A full-fine-tuning LR produces an adapter that barely moves. The team concludes "LoRA doesn't work for our task" and burns weeks on full fine-tuning they didn't need.
**Avoid:** Changing `r` and `lora_alpha` without also re-checking the LR. The effective update scale is `LR × α/r` (or `α/√r` with rsLoRA).

### 4. Assert the trainable parameter count in the training script
**Do:** Call `model.print_trainable_parameters()` and fail the job if the ratio falls outside an expected band (e.g. 0.1%–2% for a 7B model at `r=16`).
**Why:** Wrong `target_modules` names (GPT-2's `c_attn` vs Llama's `q_proj`) produce a silent run that trains almost nothing. You find out after hours of GPU time and a flat eval curve.
**Avoid:** Trusting that a dropping training loss means the adapter attached where you intended.

### 5. Pin the base model revision, tokenizer and chat template with the adapter
**Do:** Record the base model's commit hash, tokenizer version and exact chat template in the adapter's metadata or registry entry. Have the server refuse to load a mismatched pair.
**Why:** An adapter is a delta against *one specific* set of weights. Loaded onto `Llama-3.1-8B-Instruct` instead of `Llama-3.1-8B`, or served with a different template, it produces subtly wrong output that passes health checks.
**Avoid:** `base_model_name_or_path: "meta-llama/..."` with no revision, pointing at a Hub repo that can change underneath you.

### 6. Size rank to data, and stop early
**Do:** Start at `r=16` for a few thousand examples. Go higher only when eval loss plateaus above the target while training loss keeps falling slowly (a capacity limit). Train 1–3 epochs and pick the checkpoint by eval loss, not the last one.
**Why:** Raschka found that doubling passes over Alpaca *lowered* benchmark scores. High rank on small data memorizes. The symptom in production is the model repeating training examples verbatim.
**Avoid:** `r=256` on 800 examples because "more capacity is safer".

### 7. Merge into a bf16 base, never into the quantized one
**Do:** After QLoRA training, reload the base in bf16, merge the adapter, then quantize the merged model with a proper method (GPTQ/AWQ/GGUF) and evaluate *that* artifact.
**Why:** Merging into NF4 weights, or naively re-quantizing, degrades quality in ways training eval never showed. The model you tested is not the model you shipped.
**Avoid:** Evaluating the unmerged 4-bit base plus adapter, then shipping a merged and re-quantized file without re-running evals.

### 8. Decide merge vs unmerged per deployment, from measurements
**Do:** Merge when one adapter serves most traffic. Keep adapters unmerged when you have many tenants and a batched-LoRA engine (vLLM, LoRAX, TensorRT-LLM). Benchmark both at your real batch sizes.
**Why:** Unmerged adapters add small extra matmuls per token and cost cache pressure. Merged adapters cost a full model copy per variant and a cold start to swap.
**Avoid:** Running a single-tenant production model unmerged "for flexibility" and paying the latency tax forever.

### 9. Configure serving limits to match your real adapters
**Do:** Set vLLM `--max-lora-rank` to your highest adapter rank, `--max-loras` to your concurrent hot set, and `--max-cpu-loras` to the warm set. Enforce a rank ceiling in CI before an adapter reaches the registry.
**Why:** A rank-64 adapter fails to load on a server configured for 16, and it shows up as an incident when a tenant goes live. Setting the limit "just in case" high reserves GPU memory that could have been KV cache.
**Avoid:** Letting each training team pick rank freely with no contract with the serving team.

### 10. Evaluate for forgetting, not only for the target task
**Do:** Run a small held-out general suite (instruction following, refusal behavior, a reasoning set) against every adapter, beside the task eval.
**Why:** Biderman et al. show LoRA "forgets less" than full fine-tuning, but it still forgets. A support-bot adapter that stops following safety instructions is a security incident, not a quality blip.
**Avoid:** Promoting an adapter because the task metric went up 8 points.

## Anti-patterns to recognize

- **Fine-tuning as a knowledge base**: the team trains an adapter on the product docs so the model "knows" them. It hallucinates with more confidence, can't cite, and goes stale on the next release. Use RAG for facts and LoRA for format, tone and behavior.
- **The eval-on-training-distribution trap**: the eval set is a random 10% split of the same synthetic data. Near-duplicates leak, scores look superb, and real user traffic looks nothing like it. Build the eval set from real, deduplicated production inputs, and freeze it.
- **Pickle adapters from strangers**: pulling `adapter_model.bin` or `.ckpt` LoRAs from community hubs and loading them with `torch.load`. Pickle files can execute arbitrary code on load. Accept only `.safetensors` and scan or sandbox third-party artifacts.
- **Runtime adapter loading on a public endpoint**: turning on vLLM's `/v1/load_lora_adapter` (`VLLM_ALLOW_RUNTIME_LORA_UPDATING=True`) in a shared cluster. The vLLM docs warn against this outside a fully trusted, isolated environment. Load adapters through a controlled deploy path instead.
- **Stacking adapters and hoping they compose**: merging a "legal tone" adapter with a "JSON output" adapter by adding the deltas. The deltas interfere, and each one's behavior degrades unpredictably. Train one adapter on the combined data, or evaluate any merge (TIES/DARE-style) as a new model.
- **Hyperparameter archaeology**: `alpha = 2 × r`, `lora_dropout=0.05` and `r=8` copied from a 2023 notebook, never questioned. Raschka's best run used `alpha = 0.5 × r` at `r=256`. Treat `α`, `r` and LR as one coupled knob and sweep them.
- **Orphaned adapters**: forty adapters in a bucket named `final_v2_really_final`, none tied to a base revision, dataset hash or eval report. When the base model is upgraded, nobody knows which ones must be retrained. Register adapters like any build artifact.

## Real-world usage patterns

**Structured-output adapter for an internal agent platform.** A mid-size company runs Llama-class 8B models behind tool-calling agents. Prompting alone produced invalid tool-call JSON on a few percent of calls. A rank-16 all-linear adapter trained on about 5K validated traces fixed format errors, and it was merged for zero-overhead serving. *Lesson:* the win came from cleaning the traces. Rejecting every example with a malformed call mattered more than any hyperparameter.

**Many-tenant serving on one GPU.** Predibase's [LoRA Land](https://arxiv.org/abs/2405.00732) trained 310 QLoRA adapters across 10 base models and 31 tasks, averaging 10 points above GPT-4 on those narrow tasks. It served 25 Mistral-7B adapters from a single A100 via LoRAX. *Lesson:* specialized small adapters beat a frontier generalist on *narrow* tasks. The economics depend on the batched-LoRA engine and adapter cache, not on training.

**RL post-training on a budget.** Research and startup teams run GRPO-style reasoning tuning with LoRA adapters, because policy-gradient signal carries so little information per episode that LoRA matches full fine-tuning even at rank 1 (LoRA Without Regret). *Lesson:* for RL, the bottleneck is rollout generation, not trainable parameters. Spend the budget on sampling throughput, not on rank.

**Diffusion style marketplace.** Image-generation products let users pick style or character LoRAs at request time, often several per image with per-adapter weights. *Lesson:* adapter weights are user-facing parameters. Clamp them, because stacking three LoRAs at weight 1.0 each routinely produces broken output that users report as "the model is bad".

## Operational checklist

- Does the training job assert the trainable parameter ratio, and fail on zero or out-of-band values?
- Is every adapter registered with its base model revision, tokenizer, chat template, dataset hash and eval report, and does the server refuse a mismatched base?
- Was the *shipped* artifact (merged, re-quantized) evaluated, not only the training-time adapter?
- Do the eval results include a general-capability and safety-behavior suite, not only the task metric?
- Monitoring: are per-adapter request rate, latency (p50/p99), adapter cache hit/miss and load time, and GPU memory split (weights vs KV cache vs adapters) on a dashboard?
- Failure handling: when an adapter fails to load (rank too high, file missing, base mismatch), does the request fail loudly or fall back to the base model? Is the fallback what the product wants, and is it tested?
- Security: are only `.safetensors` adapters accepted, and is runtime adapter loading disabled or restricted to a trusted control plane?
- Cost: is `--max-lora-rank` set to the real maximum, and is there a rank ceiling in CI? Are idle tenants' adapters evicted rather than pinned on the GPU?
- Onboarding: can a new engineer find, in one place, which base model each adapter targets, how to retrain it, and who owns its eval set?

## How this topic typically evolves in a codebase

Teams start with a notebook: one QLoRA run on a single GPU, `r=8` on `q_proj`/`v_proj`, eval by eyeballing ten outputs, and a merged checkpoint uploaded by hand. It works well enough to demo, and that success creates demand for a second and third adapter.

The middle phase is where the pain lives. The adapters multiply, and each has its own script, hyperparameters and base revision. Then the base model upgrades (Llama 3 to 3.1, for example) and every adapter must be retrained, because deltas do not transfer across base weights. This is the painful migration point. Teams that never recorded dataset versions and eval sets can't reproduce their own adapters, and teams that merged everything find they now store and serve N full model copies.

Mature setups treat adapters as build artifacts. There is one config-driven training pipeline (Axolotl, torchtune or `trl` with templated configs), a registry keyed by base revision, a frozen eval suite with a promotion gate, and a multi-LoRA serving tier with explicit rank and cache limits. Base-model upgrades become a scheduled batch retrain-and-evaluate job rather than a fire drill. At that scale, the recurring cost is data curation and evaluation, not GPUs.

## Further reading

- [LoRA Without Regret (Thinking Machines, 2025)](https://thinkingmachines.ai/blog/lora/): the current best evidence on when LoRA matches full fine-tuning, with concrete guidance on LR, target layers and batch size.
- [Practical Tips for Finetuning LLMs Using LoRA (Sebastian Raschka)](https://magazine.sebastianraschka.com/p/practical-tips-for-finetuning-llms): hundreds of controlled runs on QLoRA cost, epochs, rank/alpha and layer selection, with numbers.
- [LoRA Learns Less and Forgets Less (Biderman et al., 2024)](https://arxiv.org/abs/2405.09673): the clearest statement of where LoRA falls short (continued pretraining, code/math at scale) and why.
- [LoRA Land technical report (Predibase, 2024)](https://arxiv.org/abs/2405.00732): 310 adapters benchmarked, plus the multi-LoRA serving economics that make per-task adapters viable.
- [vLLM LoRA adapters docs](https://docs.vllm.ai/en/latest/features/lora.html): the serving flags (`--max-loras`, `--max-lora-rank`, `--max-cpu-loras`) and the runtime-loading security warning you will need in production.
- [Hugging Face PEFT LoRA guide](https://huggingface.co/docs/peft/developer_guides/lora): the reference for `LoraConfig` options such as `use_rslora`, `use_dora`, `modules_to_save` and merging APIs.
