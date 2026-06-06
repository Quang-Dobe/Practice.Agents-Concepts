"""
Scaled dot-product attention from scratch in NumPy.

This file implements the core op from "Attention Is All You Need":

    Attention(Q, K, V) = softmax( Q K^T / sqrt(d_k) ) V

Pedagogical goals:
  1. Show that attention is just two matmuls with a softmax in between.
  2. Print the attention-weight matrix so you can see which token attends
     to which - this is the whole point of the diagnostic.
  3. Demonstrate causal masking (decoder-style) by zeroing future positions
     before the softmax.

No torch, no autograd, no training. Single forward pass on a 5-token toy
sequence with fixed RNG seeds so the output is reproducible.
"""

import numpy as np


def softmax(x: np.ndarray, axis: int = -1) -> np.ndarray:
    # Subtract the row max before exp - the standard numerical-stability trick.
    # Without this, large logits overflow in fp32 and produce NaN.
    shifted = x - np.max(x, axis=axis, keepdims=True)
    exp = np.exp(shifted)
    return exp / np.sum(exp, axis=axis, keepdims=True)


def scaled_dot_product_attention(
    Q: np.ndarray,
    K: np.ndarray,
    V: np.ndarray,
    mask: np.ndarray | None = None,
) -> tuple[np.ndarray, np.ndarray]:
    """
    Q: (n_q, d_k)   K: (n_k, d_k)   V: (n_k, d_v)
    mask: optional (n_q, n_k) array of 0/1 where 0 means "forbidden".

    Returns (output, weights):
      output:  (n_q, d_v)  - the value vectors blended per query
      weights: (n_q, n_k)  - the attention probabilities, one row per query
    """
    d_k = Q.shape[-1]

    # Raw compatibility scores: every query dotted with every key.
    # Shape (n_q, n_k). This is the (n^2) table that makes attention quadratic.
    scores = Q @ K.T

    # The famous 1/sqrt(d_k). Without it, as d_k grows the dot products grow
    # in variance, softmax saturates into near one-hot, and gradients vanish.
    scores = scores / np.sqrt(d_k)

    # Additive mask: put -inf where mask == 0 so those entries become exactly 0
    # after softmax. This is how causality and padding are enforced.
    if mask is not None:
        scores = np.where(mask == 0, -np.inf, scores)

    # Row-wise softmax: each query gets a probability distribution over keys.
    weights = softmax(scores, axis=-1)

    # Weighted sum of value vectors. Each output row is a convex combination
    # of value rows - a "soft dictionary lookup".
    output = weights @ V
    return output, weights


def pretty_print_weights(weights: np.ndarray, tokens: list[str]) -> None:
    # Show one row per query token, columns per key token. Reading row i
    # left-to-right tells you "what did token i look at and how much?".
    header = " " * 10 + "  ".join(f"{t:>6}" for t in tokens)
    print(header)
    for i, row in enumerate(weights):
        cells = "  ".join(f"{w:6.3f}" for w in row)
        print(f"{tokens[i]:>8}  {cells}")


def main() -> None:
    np.random.seed(0)

    # Toy sequence. The tokens are just labels for the printout; the model
    # operates on their embedding vectors below.
    tokens = ["The", "cat", "sat", "on", "mat"]
    n, d_model = len(tokens), 8
    d_k = d_v = 4  # head dimension; small so the printout is readable

    # Random "embeddings" stand in for what a real model would learn.
    X = np.random.randn(n, d_model)

    # Learned projections W_Q, W_K, W_V map embeddings into the Q/K/V
    # subspaces. In a real model these are trained; here they are random
    # so we can watch the mechanics, not the semantics.
    W_Q = np.random.randn(d_model, d_k) * 0.5
    W_K = np.random.randn(d_model, d_k) * 0.5
    W_V = np.random.randn(d_model, d_v) * 0.5

    Q = X @ W_Q
    K = X @ W_K
    V = X @ W_V

    # ---- Pass 1: bidirectional self-attention (encoder-style) ----------
    out_bi, weights_bi = scaled_dot_product_attention(Q, K, V)

    print("=== Bidirectional self-attention ===")
    print("Each row is a query token; each column is a key token.")
    print("Row i shows how much token i attended to every other token.")
    print("Rows sum to 1.\n")
    pretty_print_weights(weights_bi, tokens)
    print(f"\nOutput shape: {out_bi.shape}  (n_q x d_v)")
    print(f"Row sums (should all be 1): {weights_bi.sum(axis=1).round(6)}\n")

    # ---- Pass 2: causal mask (decoder-style) ---------------------------
    # Lower-triangular mask of shape (n, n): position i may attend to
    # positions 0..i, and nothing later. This is what makes GPT-style
    # models autoregressive: token i cannot peek at token i+1 during
    # training, so one forward pass produces n parallel next-token
    # predictions.
    causal_mask = np.tril(np.ones((n, n), dtype=int))
    out_causal, weights_causal = scaled_dot_product_attention(
        Q, K, V, mask=causal_mask
    )

    print("=== Causal (decoder-style) self-attention ===")
    print("Upper triangle is zero: future positions are forbidden.\n")
    pretty_print_weights(weights_causal, tokens)
    print(f"\nFirst row attends only to itself: {weights_causal[0].round(3)}")
    print(f"Last row can attend to everything: {weights_causal[-1].round(3)}")


if __name__ == "__main__":
    main()
