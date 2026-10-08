"""Gradient descent MVP: the smallest runnable proof of the three ideas that matter.

1. The learning rate has a hard ceiling: on the overview example the limit is lr < 2/curvature = 0.25.
2. Ill-conditioned valleys: plain GD crawls, momentum and Adam add back cheap curvature info.
3. Mini-batch SGD beats full-batch GD for the same compute (same number of per-example gradients).

Standard library only. Deterministic (fixed seed). Run: python mvp.py
"""
import math
import random

# --- Part 1: the step-size ceiling --------------------------------------------------------------
# Illustrative overview example: fit y = w*x to the single point (x=2, y=6) with squared error.
# L(w) = (2w - 6)^2  ->  L'(w) = 8w - 24  ->  curvature L'' = 8, so GD is stable only if lr < 2/8.
print("Part 1: fit y = w*x to (2, 6); the answer is w = 3; stability limit lr < 0.25")
for lr in (0.05, 0.24, 0.25, 0.26):
    w = 0.0
    for _ in range(50):
        w -= lr * (8 * w - 24)  # the whole algorithm: step against the gradient
    # Each step multiplies (w - 3) by (1 - 8*lr): |factor| < 1 shrinks, = 1 bounces, > 1 explodes.
    print(f"  lr={lr:<5} factor={1 - 8 * lr:+.2f}  w after 50 steps = {w:12.4f}")

# --- Part 2: an ill-conditioned valley ----------------------------------------------------------
# L(x, y) = 0.5 * (1*x^2 + 100*y^2). Curvatures 1 and 100 -> condition number kappa = 100.
# The steep axis (100) caps lr below 2/100 = 0.02, so the flat axis (1) shrinks by only ~2% per step.
def grad(p):
    return [1.0 * p[0], 100.0 * p[1]]


def minimise(update, steps=100):
    p, state = [1.0, 1.0], {}
    for t in range(1, steps + 1):
        g = grad(p)
        p = [p[i] - update(i, g[i], t, state) for i in range(2)]  # update() returns the step
    return 0.5 * (p[0] ** 2 + 100 * p[1] ** 2)


def plain_gd(i, g, t, s, lr=0.018):
    return lr * g


def momentum(i, g, t, s, lr=0.018, mu=0.9):
    # PyTorch form: v <- mu*v + g; step = lr*v. The velocity builds up along the consistent flat axis.
    s[i] = mu * s.get(i, 0.0) + g
    return lr * s[i]


def adam(i, g, t, s, lr=0.05, b1=0.9, b2=0.999, eps=1e-8):
    # Dividing by sqrt(v) rescales every axis to roughly unit steps: a cheap diagonal preconditioner.
    m = s[("m", i)] = b1 * s.get(("m", i), 0.0) + (1 - b1) * g
    v = s[("v", i)] = b2 * s.get(("v", i), 0.0) + (1 - b2) * g * g
    m_hat, v_hat = m / (1 - b1**t), v / (1 - b2**t)  # bias correction: m and v start at zero
    return lr * m_hat / (math.sqrt(v_hat) + eps)


print("\nPart 2: valley with curvatures 1 and 100, start (1, 1), 100 steps, loss starts at 50.5")
for name, update in (("plain GD", plain_gd), ("momentum", momentum), ("Adam", adam)):
    print(f"  {name:<9} final loss = {minimise(update):.6f}")

# --- Part 3: full-batch GD vs mini-batch SGD at equal compute -----------------------------------
# 200 noisy points from y = 3x + noise. Model y = w*x, loss = mean squared error.
random.seed(0)  # fixed seed: the output is identical on every run
xs = [random.uniform(-1, 1) for _ in range(200)]
data = [(x, 3 * x + random.gauss(0, 0.1)) for x in xs]


def mse_grad(w, batch):
    # d/dw mean((w*x - y)^2) = mean(2x(w*x - y)). Backprop would compute this for a real network.
    return sum(2 * x * (w * x - y) for x, y in batch) / len(batch)


print("\nPart 3: fit y = w*x to 200 noisy points (true w = 3), lr = 0.2, 5 epochs each")
for batch_size in (200, 10):  # 200 = full batch (1 step/epoch); 10 = mini-batch (20 steps/epoch)
    w, rng = 0.0, random.Random(1)
    for epoch in range(5):  # every epoch costs 200 per-example gradients in both cases
        rng.shuffle(data)  # reshuffle each epoch, or SGD replays the same order forever
        for start in range(0, len(data), batch_size):
            w -= 0.2 * mse_grad(w, data[start : start + batch_size])
    steps = 5 * len(data) // batch_size
    print(f"  batch={batch_size:<4} updates={steps:<4} w = {w:.4f}")
