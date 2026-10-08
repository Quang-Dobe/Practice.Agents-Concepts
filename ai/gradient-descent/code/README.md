# Gradient Descent — MVP Code

The smallest runnable demo of gradient descent. About 45 lines of actual code, comments excluded.

## What it demonstrates
- The stability ceiling `lr < 2/curvature` from `02-deep-dive.md § How`. On the overview example (curvature 8), `lr = 0.24` converges and `0.26` diverges.
- Ill-conditioning (`κ = 100`): plain GD crawls along the flat axis. Momentum and Adam's `√v̂` preconditioner get there faster.
- Mini-batch SGD vs full-batch GD on the same compute budget (200 per-example gradients per epoch). Mini-batch takes 20× more updates and wins.

## Prerequisites
Python 3.11+. Standard library only, nothing to install.

## Run it

```bash
python mvp.py
```

## Expected output

```text
Part 1: fit y = w*x to (2, 6); the answer is w = 3; stability limit lr < 0.25
  lr=0.05  factor=+0.60  w after 50 steps =       3.0000
  lr=0.24  factor=-0.92  w after 50 steps =       2.9536
  lr=0.25  factor=-1.00  w after 50 steps =       0.0000
  lr=0.26  factor=-1.08  w after 50 steps =    -137.7048
Part 2: ... plain GD 0.013221 | momentum 0.000084 | Adam 0.000896
Part 3: ... batch=200 updates=5 w = 1.4773 | batch=10 updates=100 w = 2.9759
```

## What to try next
- Change the Part 1 loop to 51 steps. `lr = 0.25` now prints `6.0000`, the other end of the bounce.
- In Part 2, raise plain GD's `lr` to `0.021` (above `2/100`) and watch the loss explode.
- Set `mu=0.0` in `momentum` and confirm it gives the same loss as plain GD.
- In Part 3, set `batch_size` to `1` (classic SGD) and see how far `w` lands from 3: that gap is the noise ball.
