# Gradient Descent — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

On an applied ML team, gradient descent is the inner loop of every training and fine-tuning job. You rarely write the update rule. You pick an optimiser, a learning rate, a schedule, a batch size and a clipping threshold, and those five numbers decide whether the run converges, stalls or turns into NaN at 3 a.m.

On an ML platform team, it is infrastructure. Optimiser state sets GPU memory and checkpoint size. Preemptible instances turn resume-correctness into a reliability issue, and scaling from 1 to 64 GPUs silently changes the effective batch size under a tuned learning rate.

In classic large-scale ML (ad click prediction, ranking, fraud scoring), it runs as online SGD on sparse logistic models fed by a stream. There the question is per-feature step size, not deep-network stability.

## Best practices

### 1. Prove the loop is wired before you tune anything
**Do:** Check the loss at initialisation (about `ln(num_classes)` for softmax cross-entropy), then overfit one small batch to near-zero loss.
**Why:** Detached graphs, frozen parameters, misaligned labels and a missing `zero_grad()` all look like "the learning rate needs tuning". These checks find them in minutes, not after a multi-day run.
**Avoid:** Launching a sweep on a pipeline that has never memorised one batch.

### 2. Start from a published recipe and tune the learning rate first
**Do:** Copy optimiser settings from a model close to yours, then sweep `lr` on a log grid in ~3× steps (`1e-4, 3e-4, 1e-3`). Google's tuning playbook: with under 10 trials, tune only `lr` for Adam; add `β₁`, then `ε`, then `β₂` as budget grows.
**Why:** `lr` dominates every other optimiser knob. Sweeping `β₂` first spends GPU budget on second-order effects.
**Avoid:** A linear grid (`1e-3, 2e-3, 3e-3`) that never reaches the stable region.

### 3. Run warmup plus decay, and step the scheduler once per optimiser step
**Do:** Warm up over the first few percent of steps, then decay (cosine or WSD). Call `scheduler.step()` after `optimizer.step()`, not per micro-batch or per epoch.
**Why:** No warmup is the classic cause of NaN in the first few hundred steps. A step-based schedule stepped per epoch turns a 2,000-step warmup into 2,000 epochs. PyTorch warns if the scheduler steps first, because the first schedule value is skipped.
**Avoid:** A constant `lr` "because the model is small".

### 4. Log gradient norm, actual learning rate and update-to-weight ratio every step
**Do:** Record the pre-clip global gradient norm, the `lr` the optimiser really used, and per-layer `‖Δθ‖/‖θ‖` (CS231n's rule of thumb: about `1e-3`).
**Why:** Gradient-norm spikes usually show up before loss spikes. A ratio near `1e-1` means a layer is overwritten every step; near `1e-6`, it is effectively frozen. Logging the real `lr` catches schedules that never ran.
**Avoid:** A 100-step moving average of loss as the only signal. It smooths away the spike you need.

### 5. Clip the global norm, then watch how often you clip
**Do:** Use `clip_grad_norm_` (1.0 is the common transformer value and GPT-3's). With fp16 `GradScaler`, call `scaler.unscale_(optimizer)` first. Log the clipped-step fraction.
**Why:** One outlier batch can otherwise apply a step many times the normal size. Clipping still-scaled gradients compares a number inflated by the loss scale against 1.0, so every step clips.
**Avoid:** A threshold that clips 100% of steps. You have silently switched to normalised-gradient descent with a different effective `lr`.

### 6. Exempt biases and norm gains from weight decay
**Do:** Build two parameter groups: matrices decay, 1-D tensors (biases, LayerNorm/RMSNorm gains) get `weight_decay=0`. nanoGPT's rule is "`dim >= 2` decays". Embeddings are disputed: nanoGPT decays them, some recipes exempt them.
**Why:** Decaying norm gains toward zero fights the normalisation layer and costs quality that no single metric flags.
**Avoid:** `AdamW(model.parameters(), weight_decay=0.1)`.

### 7. Re-tune the learning rate whenever the effective batch size changes
**Do:** Record `GPUs × per-device batch × accumulation steps` as part of the recipe. Start from a scaling rule (linear plus warmup for SGD, per Goyal et al.; roughly `√B` for Adam, per Malladi et al. 2022, which also adjusts `β` and `ε`), then run a short sweep.
**Why:** Going from 1 to 8 GPUs at the same per-device batch is an 8× batch increase, and the resulting accuracy drop looks like a data problem.
**Avoid:** "Same config, more GPUs" as a scaling plan.

### 8. Checkpoint the whole optimiser, not just the weights
**Do:** Save weights, optimiser state (`m`, `v`, step count), scheduler, `GradScaler`, RNG states and data-loader position. Load outside checkpoints with `weights_only=True` (PyTorch's default since 2.6).
**Why:** Fresh Adam state on resume restarts bias correction from `v ≈ 0`: large early steps and a loss jump. A missing data position replays seen data. On spot instances this happens weekly.
**Avoid:** Saving only `model.state_dict()` "to save disk". Budget about 3× the fp32 weight size instead.

### 9. Test that gradient accumulation equals the big batch
**Do:** Normalise token-level loss by total non-padding tokens across all micro-batches. Add a CI test: batch 32 versus 4 × 8 accumulated must give the same gradient within tolerance.
**Why:** Per-micro-batch averaging over-weights short sequences. This exact bug shipped in Hugging Face Transformers until October 2024.
**Avoid:** Trusting "equivalent" because the docs say so.

## Anti-patterns to recognize

- **Adam-with-L2 labelled AdamW**: code says `Adam(weight_decay=0.01)`, the design doc says "AdamW". The decay is rescaled by `√v̂` and barely acts (see `02-deep-dive.md § How`). Use the `AdamW` class and grep for it in review.
- **The untuned baseline**: "Switching SGD to Adam gained 3 points", with SGD run at Adam's `lr`. Without a per-optimiser sweep you are measuring tuning effort, not the optimiser. Give every arm the same trial budget, over 2–3 seeds, as AlgoPerf does.
- **Extending a finished cosine run**: the run ended at `lr ≈ 0`, so someone bumps `max_steps` and resumes. Either `lr` jumps back up and the loss spikes, or it stays near zero and nothing is learned. Plan continuations with WSD, or re-warm explicitly and treat it as a new run.
- **NaN whack-a-mole**: every divergence is answered by halving `lr` until training is stable and slow. That treats the symptom. Check warmup, `β₂`, clipping, fp16 overflow and data first, and read the per-layer gradient-norm log.
- **Silent skipped steps in fp16**: `GradScaler` skips steps with inf/NaN gradients and halves the scale. Under persistent overflow the model stops updating while the loss sits flat. Log scale and skip count, or use bf16 where hardware allows.
- **Same shuffle every epoch**: under DDP, a missing `sampler.set_epoch(epoch)` makes `DistributedSampler` replay one order forever. Training "works", slightly worse, and nobody traces it. Put the line in the shared training template.

## Real-world usage patterns

**LLM pretraining loss spikes (PaLM, OPT-175B).** PaLM 540B hit about 20 loss spikes despite clipping. The fix: restart ~100 steps before the spike and skip 200–500 batches. The same batches replayed from another checkpoint did not spike. Meta's OPT-175B logbook shows divergences handled by restarting earlier with a lower `lr`. *Lesson:* spikes come from data and parameter state together. Checkpoint frequency plus a "rewind and skip" runbook is a stability tool, not just a backup.

**Large-batch vision training.** Goyal et al. trained ResNet-50 on ImageNet in one hour on 256 GPUs at batch 8192, using linear LR scaling with a 5-epoch gradual warmup. Without warmup the rule failed early, and above about 8K accuracy dropped even with both. *Lesson:* scaling rules have a valid range and bring their own warmup requirement. Verify at every new batch size.

**Fine-tuning with gradient accumulation.** In October 2024, Unsloth and Hugging Face showed `Trainer` losses differed with accumulation on and off, because cross-entropy was normalised per micro-batch. Variants of the bug had been found and reintroduced since 2021. *Lesson:* "mathematically equivalent" settings need an equivalence test. Comparing two short loss curves is cheap.

**Online CTR with per-coordinate step sizes.** Google's 2013 "view from the trenches" paper trains streaming logistic regression over billions of sparse features with FTRL-Proximal and an AdaGrad-style learning rate per coordinate, so rare features take larger steps. *Lesson:* adaptive step sizes solved a sparse-data problem before they were a deep-learning default. When feature frequencies span orders of magnitude, one global `lr` is wrong for most of them.

## Operational checklist

- Does a smoke job check initial loss and pass an overfit-one-batch test?
- Monitoring: are loss, actual `lr`, pre-clip gradient norm, clip fraction and (fp16) loss scale and skipped steps logged per step, unsmoothed?
- Is weight decay set through parameter groups with biases and norm gains exempt, using the `AdamW` class?
- Failure handling: does a checkpoint hold optimiser, scheduler, scaler, RNG and data position, and has a kill-and-resume test shown a smooth loss curve?
- Is there a written loss-spike runbook (rewind N steps, skip batches, lower `lr`), and does checkpoint frequency support it?
- Is the effective batch size stored with each run, and does changing it trigger an LR re-sweep?
- Security: are external checkpoints loaded as `safetensors` or with `weights_only=True`, never as raw pickle?
- Cost: is optimiser-state memory (about 12 of the 16 bytes per parameter) budgeted, and do sweeps run on short proxy runs first?
- Onboarding: is the recipe of record (optimiser, `lr`, betas, warmup, schedule, clip, batch size) in one config file?

## How this topic typically evolves in a codebase

Teams start in a notebook: `Adam(model.parameters(), lr=1e-3)`, fixed epochs, no schedule, loss printed every 100 steps. It works on the first dataset, and the numbers harden into folklore copied into the next three projects.

The painful phase is the first multi-GPU move. The effective batch changes and the old `lr` breaks. Preemption exposes resume bugs. Gradient accumulation arrives to fit larger models and brings normalisation bugs. Optimiser state becomes the largest item in GPU memory, which forces ZeRO or FSDP sharding. Checkpoints then become sharded and tied to one GPU count, and resharding for a different cluster size is the migration nobody planned.

Mature setups treat the optimiser as config: one recipe file per model family, a shared parameter-group builder, gradient-norm alerts, tested resume, and LR sweeps on short proxy runs. Teams training several model sizes adopt μP (Tensor Programs V) so hyperparameters tuned small transfer to large, and continuous trainers adopt WSD so runs can be extended. By then the recurring cost is tuning and evaluation compute.

## Further reading

- [Deep Learning Tuning Playbook (Google Research)](https://github.com/google-research/tuning_playbook): the most practical guide to batch size, LR sweeps and reading training curves.
- [A Recipe for Training Neural Networks (Karpathy, 2019)](https://karpathy.github.io/2019/04/25/recipe/): the "verify loss at init, overfit one batch" discipline that catches wiring bugs before they cost GPU-days.
- [OPT-175B logbook (Meta)](https://github.com/facebookresearch/metaseq/tree/main/projects/OPT/chronicles): a day-by-day record of divergences, restarts and LR changes, the closest thing to a public post-mortem on optimiser instability.
- [PaLM, § Training instability (arXiv:2204.02311)](https://arxiv.org/abs/2204.02311): the rewind-and-skip mitigation and the evidence that spikes are not just bad data.
- [Fixing Gradient Accumulation (Hugging Face, 2024)](https://huggingface.co/blog/gradient_accumulation): how a "mathematically equivalent" feature drifted from the maths in a widely used library.
