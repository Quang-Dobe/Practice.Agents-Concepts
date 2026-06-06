# Attention Mechanism - MVP Code

The smallest runnable demo of scaled dot-product attention: about 60 lines of actual code in pure NumPy, no torch.

## What it demonstrates
- Scaled dot-product attention as two matmuls with a softmax in the middle (`02-deep-dive.md` -> What).
- The `1/sqrt(d_k)` scaling that keeps softmax in its useful regime.
- Row-wise attention weights printed as a matrix so you can read off "which token attended to which".
- Causal masking: a lower-triangular mask zeroes out future positions, turning the same op into decoder-style autoregressive attention.

## Prerequisites
- Python 3.11+
- One dependency: `pip install numpy`

NumPy-only on purpose. Torch would add autograd and fused kernels that you do not need to see the mechanics. The cost is no GPU and no backprop, which is fine for a forward-pass demo.

## Run it

```bash
python mvp.py
```

## Expected output
Two printed attention-weight matrices on a 5-token toy sequence (`The cat sat on mat`). The first is bidirectional - every row sums to 1 and uses the full sequence. The second has a strict lower-triangular pattern: token 0 attends only to itself, token 1 splits weight between tokens 0 and 1, and so on. The output tensor shape is reported as `(5, 4)` = `(n_q, d_v)`.

## What to try next
- Change `np.random.seed(0)` to a different seed and watch the attention pattern shift - the weights are entirely a function of the random projections, since nothing was trained.
- Remove the `/ np.sqrt(d_k)` line and set `d_k = 64`. Watch the weight rows collapse toward one-hot as softmax saturates.
- Replace the causal mask with a sliding-window mask (`np.abs(i - j) <= 1`) and see how each token sees only its immediate neighbours.
- Add a second "head": run attention twice with different `W_Q, W_K, W_V` and concatenate the outputs - that is multi-head attention in 5 extra lines.
