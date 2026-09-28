"""LoRA from scratch on one linear layer, with NumPy only.

What this file proves:
  1. With B = 0 at init, the adapted layer computes exactly what the base layer does.
  2. Training only A and B (r * (d + k) numbers) can learn a low-rank weight change
     while W0 (d * k numbers) is never touched.
  3. Merging W' = W0 + (alpha / r) * B @ A gives the same output as the unmerged
     two-branch forward pass, so inference has no extra cost.
  4. Removing the adapter gives back the untouched base model.
"""
import numpy as np

rng = np.random.default_rng(0)
d, k, r, alpha = 64, 64, 4, 8  # output dim, input dim, LoRA rank, LoRA alpha
scale = alpha / r              # the (alpha / r) factor from the paper

# The "pretrained" weight. Frozen: we never assign to it after this line.
W0 = rng.normal(0, 1 / np.sqrt(k), (d, k))
W0_before = W0.copy()

# The "task" needs a weight change of rank 2. LoRA's bet: fine-tuning deltas are low rank.
true_delta = rng.normal(0, 0.3, (d, 2)) @ rng.normal(0, 0.3, (2, k))
X = rng.normal(size=(512, k))            # 512 training inputs
Y = X @ (W0 + true_delta).T              # targets the fine-tuned layer should produce

# Adapter init: A random (down-projection), B zero (up-projection) -> delta W = 0 at step 0.
A = rng.normal(0, 1 / np.sqrt(k), (r, k))
B = np.zeros((d, r))


def forward(x, A, B):
    # Unmerged LoRA forward: frozen path + scaled low-rank side branch.
    return x @ W0.T + scale * (x @ A.T) @ B.T


print(f"full fine-tune params  : {d * k}")
print(f"LoRA params (r={r})      : {r * (d + k)}  ({r * (d + k) / (d * k):.1%} of full)")
print("step 0 output == base  :", np.array_equal(forward(X, A, B), X @ W0.T))

lr = 0.002
for step in range(301):
    Z = X @ A.T                              # (n, r) inputs projected into the rank-r subspace
    err = forward(X, A, B) - Y               # (n, d) prediction error
    loss = 0.5 * np.mean(np.sum(err**2, axis=1))
    g = err / len(X)                         # dLoss/dOutput
    grad_B = scale * g.T @ Z                 # only A and B get gradients ...
    grad_A = scale * (g @ B).T @ X           # ... W0 gets none, so no optimizer state for it
    if step == 0:
        # B = 0 makes A's gradient exactly zero on the first step (the asymmetry LoRA+ targets).
        print(f"step 0 |grad_A|        : {np.abs(grad_A).max():.1f}")
    if step % 50 == 0:
        print(f"step {step:3d} loss          : {loss:.5f}")
    A -= lr * grad_A
    B -= lr * grad_B

print("W0 unchanged           :", np.array_equal(W0, W0_before))

# Merge for deployment: fold the adapter into one ordinary weight matrix.
W_merged = W0 + scale * B @ A
x_new = rng.normal(size=(3, k))
print("merged == unmerged     :", np.allclose(x_new @ W_merged.T, forward(x_new, A, B)))

# Peel the adapter off: zero B again and you are back to the base model.
print("adapter removed == base:", np.array_equal(forward(x_new, A, np.zeros_like(B)), x_new @ W0.T))
