# Eventual Consistency — MVP Code

The smallest runnable demo of eventual consistency: three replicas accept writes independently, diverge, then converge in one anti-entropy round. ~70 lines of actual code.

## What it demonstrates

- **Convergence** — replicas that saw the same writes reach the same state regardless of exchange order (`gossip()` is commutative and idempotent).
- **LWW silently drops concurrent updates** — the classic footgun from `02-deep-dive.md` and `03-practice.md` best-practice #4.
- **CRDT (G-Counter) preserves every concurrent write** — strong eventual consistency: no coordination, no lost increments.
- **Anti-entropy is idempotent** — running `gossip()` twice changes nothing.

## Run it (Python 3.11+, stdlib only)

```bash
python mvp.py
```

## Expected output

Six labeled snapshots. Key beats:

```
--- after independent writes — DIVERGED ---
  A: city='Hanoi'   @ts1  likes=2  slots={'A': 2}
  B: city='Tokyo'   @ts2  likes=1  slots={'B': 1}
  C: city='Berlin'  @ts3  likes=3  slots={'C': 3}

--- after gossip round 1 — CONVERGED ---
  A/B/C: city='Berlin' @ts3  likes=6  slots={'A': 2, 'B': 1, 'C': 3}
```

`likes` sums to 6 (every increment kept). `city` is `'Berlin'` because ts=3 beat ts=1 and ts=2 — Hanoi and Tokyo are gone forever.

## What to try next

- Replace `GCounter` with a naive `x = x + 1` counter and watch increments disappear on convergence.
- Comment out the second `gossip()` call — state is already identical, proving idempotence.
