# LoRA — MVP Code

The smallest runnable demo of LoRA: one frozen linear layer plus a trained `B·A` adapter, in NumPy with hand-written gradients. About 35 lines of actual code, comments excluded.

## What it demonstrates
- `B = 0` init: at step 0 the adapted layer is bit-identical to the base, and `A` gets zero gradient (the asymmetry LoRA+ targets).
- Only `A` and `B` (`r·(d+k)` = 512 numbers) train; `W₀` (`d·k` = 4096) never changes.
- A rank-4 adapter learns a rank-2 weight change, the low-rank bet from `02-deep-dive.md`.
- Merging `W₀ + (α/r)·B·A` gives the same output as the unmerged two-branch forward; zeroing `B` restores the base.

## Prerequisites
Python 3.11+, `pip install numpy`

## Run it

```bash
python mvp.py
```

## Expected output

```text
full fine-tune params  : 4096
LoRA params (r=4)      : 512  (12.5% of full)
step 0 output == base  : True
step 0 |grad_A|        : 0.0
step   0 loss          : 37.64295
...
step 300 loss          : 0.31331
W0 unchanged           : True
merged == unmerged     : True
adapter removed == base: True
```

## What to try next
- Set `r = 1` and watch the loss stall: a rank-1 adapter can't express a rank-2 change.
- Change `(d, 2)` / `(2, k)` in `true_delta` to rank 16 and see where `r = 4` hits its capacity ceiling.
- Initialize `B` randomly instead of zeros; "step 0 output == base" turns `False`.
- Double `alpha` without touching `lr` and see how the effective step size `lr × α/r` changes convergence.
