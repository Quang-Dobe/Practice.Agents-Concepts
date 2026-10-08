# Gradient Descent — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Gradient descent is a **first-order iterative method** for minimising a differentiable objective `L: ℝⁿ → ℝ`:

```text
θ_{t+1} = θ_t − η_t · ∇L(θ_t)
```

"First-order" means it uses only the gradient, never the Hessian. In machine learning, `L` is the empirical risk `(1/N) Σ ℓ(θ; xᵢ, yᵢ)`. Stochastic variants replace the full gradient with an unbiased mini-batch estimate `g_t = (1/|B|) Σ_{i∈B} ∇ℓ(θ_t; xᵢ, yᵢ)`. Momentum, RMSProp, Adam and AdamW all run this loop. They differ only in how they turn `g_t` into a step.

### The core building blocks

- **Objective `L(θ)`**: a scalar loss that is differentiable almost everywhere. ReLU kinks are fine.
- **Gradient oracle**: reverse-mode autodiff (backprop). A backward pass costs roughly 2× the forward pass.
- **Learning rate `η` and schedule `η_t`**: the step size, usually changed during training by warmup and decay.
- **Batch size `B`**: controls gradient noise. The variance of `g_t` scales as `σ²/B`.
- **Update rule with state**: SGD keeps no state, momentum keeps one vector, and Adam keeps two.

Convergence proofs assume the objective is **L-smooth** (its gradient is Lipschitz with constant `L`) and sometimes **μ-strongly convex**. The ratio `κ = L/μ` is the **condition number**.

### How it relates to the broader landscape

Gradient descent is one of several iterative numerical optimisation methods. Its siblings are:

- **Closed-form solvers**, such as least squares via QR.
- **Second-order methods**: Newton, and quasi-Newton L-BFGS, which approximates curvature from about 5–20 recent gradient pairs.
- **Derivative-free search**: grid search, Bayesian search and CMA-ES.

Matrix-preconditioned optimisers (Shampoo, Muon) sit between first and second order. They use more structure than a plain gradient but never form the Hessian.

## Where

### Where it runs / lives in the stack

It runs inside the **training loop**, in the same process and on the same accelerator as the model. Autograd fills each parameter's `.grad`, then the optimiser updates the parameters in place. In data-parallel training, a gradient **all-reduce** runs between the backward pass and the optimiser step. Optimiser state can be sharded across devices (DeepSpeed ZeRO, PyTorch FSDP). Serving a model takes no gradient steps, so gradient descent does not run at inference.

### Where you typically encounter it

- **PyTorch `torch.optim`**: `SGD`, `Adam`, `AdamW`, and `Muon`. Muon was added in 2.9 and supports 2-D parameters only.
- **JAX / Optax**: optimisers built as composable gradient transformations.
- **Hugging Face `Trainer`**: defaults to `adamw_torch` with linear warmup and decay.
- **scikit-learn**: `SGDClassifier` / `SGDRegressor`, and `MLPClassifier(solver='adam')`.
- **LLM pretraining stacks** (Megatron-LM, DeepSpeed): AdamW with warmup, gradient clipping and sharded optimiser state.

### Ecosystem and tooling

- **For optimisers**: `torch.optim`, Optax, and bitsandbytes 8-bit Adam, which shrinks optimiser state.
- **For schedules**: `torch.optim.lr_scheduler` and the `get_cosine_schedule_with_warmup` helper in Transformers.
- **For memory at scale**: DeepSpeed ZeRO stages 1–3 and PyTorch FSDP.
- **For diagnostics**: logging loss, learning rate and global gradient norm to TensorBoard or Weights & Biases.
- **For comparing optimisers**: the MLCommons [AlgoPerf](https://arxiv.org/abs/2502.15015) benchmark.

## When

### When the topic emerged and why

- **1847**: Cauchy proposes steepest descent.
- **1951**: Robbins and Monro publish stochastic approximation, the root of SGD. Their conditions `Σηₜ = ∞, Σηₜ² < ∞` still say when SGD converges exactly.
- **1964 / 1983**: Polyak introduces heavy-ball momentum. Nesterov's accelerated gradient then reaches the optimal `O(1/k²)` rate on smooth convex functions.
- **1986**: Rumelhart, Hinton and Williams popularise backprop plus gradient descent for multi-layer networks.
- **2011–2014**: adaptive methods arrive. AdaGrad (2011), RMSProp (Hinton's 2012 lecture, never formally published), and Adam ([arXiv:1412.6980](https://arxiv.org/abs/1412.6980)).
- **2017**: AdamW ([arXiv:1711.05101](https://arxiv.org/abs/1711.05101)) decouples weight decay. [Goyal et al.](https://arxiv.org/abs/1706.02677) train ResNet-50 at batch size 8192 in one hour using linear LR scaling plus warmup.
- **2024–2025**: Muon (Keller Jordan) orthogonalises momentum for weight matrices. Moonshot AI reports [about 2× compute efficiency over AdamW](https://arxiv.org/abs/2502.16982) at LLM scale.

Before deep learning, small models were fit in closed form or with Newton-type methods. Gradient descent won as models grew, because each step costs O(n) and it tolerates noisy mini-batch gradients.

### When to use it in a project

Reach for it when…

- there are too many parameters for a Hessian. At 10⁹ parameters, a dense Hessian has 10¹⁸ entries.
- the data must be streamed in mini-batches.
- the loss is differentiable and autodiff is available.
- you want a minimum that generalises, not a certified optimum.

### When NOT to use it

Avoid it when…

- a cheap closed form exists, such as least squares with a few thousand features (`numpy.linalg.lstsq`).
- the problem is small, smooth and deterministic. L-BFGS or Newton (`scipy.optimize.minimize`) needs far fewer iterations.
- the objective is discrete, such as a hyperparameter or architecture choice.
- hard constraints dominate. Use an LP, QP or convex solver.
- you need proof of a global optimum on a non-convex problem.

## How

### How it works under the hood

One training step:

1. **Sample** a mini-batch `B`.
2. **Forward pass**: compute the loss. Autograd records the graph.
3. **Backward pass**: apply the chain rule in reverse to get `∂ℓ/∂θ` for every parameter.
4. **All-reduce** the gradients across data-parallel workers.
5. **Clip** the global gradient norm, if enabled. GPT-3 clipped at 1.0.
6. **Optimiser step**: combine `g` with the stored state into an update and apply it.
7. **Scheduler step**: update `η`.
8. **Zero the gradients**. PyTorch accumulates `.grad` by default.

**Update rules**:

| Optimiser | Update | Extra state / param |
|---|---|---|
| SGD | `θ ← θ − η·g` | 0 |
| SGD + momentum (PyTorch form) | `v ← μ·v + g`; `θ ← θ − η·v` | 1 |
| RMSProp | `s ← α·s + (1−α)·g²`; `θ ← θ − η·g/(√s + ε)` | 1 |
| Adam | `m ← β₁m + (1−β₁)g`; `v ← β₂v + (1−β₂)g²`; `m̂ = m/(1−β₁ᵗ)`, `v̂ = v/(1−β₂ᵗ)`; `θ ← θ − η·m̂/(√v̂ + ε)` | 2 |
| AdamW | Adam, plus a separate `θ ← θ − η·λ·θ` that never enters `m` or `v` | 2 |

PyTorch's [AdamW defaults](https://docs.pytorch.org/docs/2.9/generated/torch.optim.AdamW.html) are `lr=1e-3, betas=(0.9, 0.999), eps=1e-8, weight_decay=1e-2`. Plain `Adam` has `weight_decay=0`. LLM recipes usually lower `β₂`. [GPT-3](https://arxiv.org/abs/2005.14165), for example, used `β₂=0.95`, weight decay 0.1, a warmup over 375M tokens, and then cosine decay.

**Why the step size has a hard ceiling** (Illustrative, the overview example). The loss is `L(w) = (2w − 6)²`, so `L'(w) = 8w − 24` and the curvature is `L'' = 8`. Each step gives `w − 3 ← (1 − 8η)(w − 3)`. Training converges only if `|1 − 8η| < 1`, which means `0 < η < 0.25 = 2/L''`. At 0.25 the factor is exactly −1, so `w` bounces forever. In general, gradient descent on a quadratic is stable only if `η < 2/λ_max(H)`.

**Why ill-conditioning hurts.** Each curvature direction contracts by `(1 − η·λᵢ)` per step. Because `λ_max` caps `η`, the flattest direction shrinks by only `1 − 1/κ` per step. The cost on strongly convex problems is therefore `O(κ·log(1/ε))` iterations. Momentum cuts this to `O(√κ·log(1/ε))`. Adam's division by `√v̂` is a **diagonal preconditioner**, a cheap fix for ill-conditioning that lines up with the parameter axes.

**Stochastic noise.** With a constant `η`, SGD settles in a "noise ball" whose size grows with `η·σ²/B`. Decaying `η` shrinks that ball. On non-convex losses the only guarantee is reaching a near-stationary point, where `‖∇L‖` is small.

**Saddles, not local minima.** A critical point is a minimum only if all `n` Hessian eigenvalues are positive. [Dauphin et al. (arXiv:1406.2572)](https://arxiv.org/abs/1406.2572) argue that in high dimensions saddle points vastly outnumber local minima, and that high-error local minima are rare. Part of their evidence comes from small networks. In practice a saddle shows up as a long plateau with a near-zero gradient. Mini-batch noise and momentum help the run escape it.

**Edge of stability.** [Cohen et al. (ICLR 2021)](https://arxiv.org/abs/2103.00065) found that full-batch gradient descent on neural nets raises the sharpness `λ_max` until it hovers just above `2/η`. The loss then wobbles over short windows but still falls over long ones. So `2/η` is more than a ceiling the network stays under: training settles right at it.

**Schedules.** Warmup ramps `η` up from near zero over the first few percent of steps. Early on, Adam's `v` estimates are unreliable and the loss surface is sharp. Cosine decay or **WSD** (warmup–stable–decay) follows. WSD holds `η` constant and decays only at the end, so you can keep training without fixing the step count in advance ([arXiv:2410.05192](https://arxiv.org/abs/2410.05192)).

**Vanishing and exploding gradients** come from backprop, not from gradient descent. Multiplying Jacobians across many layers can shrink or grow the gradient exponentially. Residual connections, normalisation, careful initialisation and clipping keep the gradient within a usable range.

### Key trade-offs

| Design choice | Gained | Given up |
|---|---|---|
| Full-batch vs mini-batch | Exact, deterministic gradient | O(N) per step; no noise to escape saddles |
| Larger batch | Lower variance, better accelerator use | Diminishing returns past the "critical batch size" (McCandlish et al., 2018); LR must be rescaled and warmed up |
| Higher learning rate | Faster progress, some implicit regularisation | Divergence once `η > 2/λ_max` |
| Momentum | `√κ` speed-up; smoother steps under noise | One extra state vector; overshoot |
| Adam/AdamW vs SGD+momentum | Less tuning; handles gradient scales that differ across layers; the transformer default | 2 state vectors; worse generalisation on some vision tasks (Wilson et al., 2017); non-convergence counterexamples (Reddi et al., 2018) |
| AdamW vs Adam + L2 | Decay is not divided by `√v̂`, so `η` and `λ` tune independently | PyTorch's different defaults (0 vs 0.01) invite mistakes |
| Muon / Shampoo | Matrix-aware preconditioning, ~2× compute efficiency reported for Muon | Extra matmuls (PyTorch Muon defaults to 5 Newton–Schulz steps); Muon handles 2-D weights only, so it needs AdamW for everything else |

**Memory.** Mixed-precision Adam costs about **16 bytes per parameter** ([ZeRO, arXiv:1910.02054](https://arxiv.org/abs/1910.02054)):

- 2 bytes for fp16 weights
- 2 bytes for fp16 gradients
- 12 bytes for fp32 master weights plus `m` and `v`

A 7B-parameter model therefore needs about 112 GB before activations.

### Common failure modes

- **NaN loss in the first few hundred steps.** Cause: `η` is above the stability limit, often because there is no warmup.
- **Loss flat from step 1.** Cause: `η` is too small, or gradients vanish (dead ReLUs, a deep unnormalised stack).
- **Mid-run loss spikes in LLM training.** Cause: sharpness crosses `2/η`, an outlier batch arrives, or `v` goes stale with `β₂=0.999`.
- **Steps grow every iteration.** Cause: a missing `optimizer.zero_grad()` lets gradients accumulate.
- **Weight decay does nothing visible with Adam.** Cause: `Adam(weight_decay=…)` applies coupled L2, which `√v̂` then rescales. Use AdamW.
- **Accuracy drops after an 8× batch-size increase.** Cause: `η` was not rescaled to match the larger batch.
- **Gradients silently become zero in fp16.** Cause: underflow. Use `GradScaler` or bf16.
- **Loss jumps after resuming from a checkpoint.** Cause: the optimiser's `m`/`v`/step count and the scheduler state were not saved.

## Why

### Why it exists

Training means minimising a function of 10⁶ to 10¹² variables. Exact second-order methods need O(n²) memory and up to O(n³) time per step. Reverse-mode autodiff, by contrast, delivers the full gradient for about the cost of a forward pass. Gradient descent is the cheapest update that uses it: O(n) per step. Stochastic gradients also make each step's cost independent of the dataset size `N`.

[Bottou & Bousquet (NIPS 2007)](https://papers.nips.cc/paper/3323-the-tradeoffs-of-large-scale-learning) showed why this matters. When compute is the binding constraint, optimising below the model's estimation error is wasted work. A "mediocre" optimiser like SGD therefore generalises best for a given compute budget.

### Why it looks the way it does

The obvious alternative is **Newton's method**, `θ ← θ − H⁻¹g`. It is unaffected by how parameters are scaled and converges quadratically near a minimum. It loses at deep-learning scale for three reasons:

- At 10⁹ parameters, `H` would take about 4 EB in fp32.
- Mini-batch Hessian estimates are noisy.
- On non-convex losses, `H` has negative eigenvalues, so Newton steps are *attracted* to saddles. Dauphin et al. designed saddle-free Newton to fix exactly this.

Gradient descent drops curvature information in exchange for cheap steps. The non-obvious point is that **every later optimiser adds some curvature back, cheaply**:

- Momentum approximates acceleration along consistent directions.
- Adam's `√v̂` is a diagonal preconditioner.
- Shampoo and Muon precondition whole weight matrices.

Each step up this ladder pays memory or FLOPs for better conditioning. A second point is that **noise is useful**. Even if full-batch gradient descent cost nothing extra, it would lose the mini-batch noise that helps escape saddles and acts as implicit regularisation.

Adam's shape has its own reasoning. Because `m̂/√v̂` is roughly unit-scale, each step moves parameters by about `η` regardless of the raw gradient's size. That makes `η` comparable across layers. Bias correction exists because `m` and `v` start at zero.

### Why it matters now

In 2026 the core is stable but the edges are moving:

- AdamW with warmup and decay is still the default recipe for transformers.
- PyTorch 2.9 shipped `torch.optim.Muon`.
- Moonshot trained its Moonlight MoE model with Muon.
- In the first AlgoPerf competition ([ICLR 2025](https://arxiv.org/abs/2502.15015)), Distributed Shampoo won the external-tuning track and Schedule-Free AdamW won the self-tuning track.

Optimiser state often takes more memory than the weights, which drives LoRA, 8-bit optimisers and ZeRO. Debugging a diverging run still comes down to a few quantities: `η`, `β₂`, warmup length, the clipping threshold and the gradient norm.

## Open questions / things to verify in practice

- Does the `η < 2/L''` boundary hold exactly in `code/mvp.py`? Try `lr = 0.24` vs `0.26` on the overview example.
- When batch size doubles, how far does the best `η` move for SGD+momentum compared with Adam?
- Is a plateau on a tiny network really a saddle? Check the smallest Hessian eigenvalue there.
- Does warmup matter for a small model, or only at large scale?
- Does the global gradient norm warn of a loss spike earlier than the loss curve does?
