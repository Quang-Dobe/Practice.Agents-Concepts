# CAP Theorem — MVP

A simulated 3-node replicated key-value store that makes the CAP trade-off visible in one process. No networking, no dependencies — three in-memory `Node` objects and a "network" that can drop messages between specified pairs.

## What this shows

- Quorum-based **CP** writes/reads (W=2, R=2, N=3) succeeding on the majority side of a partition and **explicitly refusing** on the minority side.
- **AP** writes succeeding on both sides of the same partition, producing **divergent** values for the same key.
- A post-heal **anti-entropy** sweep that reconciles the divergent state by last-write-wins, including the silent data loss that comes with LWW.
- The same code path under no partition: both modes happily agree, which is why CAP only bites *during* a partition.

## Run it

```bash
python3 mvp.py
```

Python 3.11+, standard library only.

## Read the output

Scan the `[step N]` labels in order. The load-bearing moments are **step 5**, where the CP coordinator on the minority side (`n3`) refuses both a put and a get because it cannot reach a majority — that is the C-over-A choice in code — and **step 6**, where AP writes on both sides of the same partition return different values for the same key, then **step 7**, where the post-heal sweep converges everyone to a single value and one of the two concurrent writes is silently lost.

## What to try next

- Change `cp_put`'s quorum threshold from `2` to `1` and observe that the minority side stops refusing — and that you've just turned CP into AP.
- Comment out the `anti_entropy(nodes)` call in step 7 and watch the cluster stay divergent forever after the heal.
- Swap the order of the two step-6 writes (n3 first, then n1) and notice that the "winner" of LWW changes — because the resolution rule is timestamp, not intent.
- Call `net.isolate("n2")` instead of `n3` and confirm the symmetry: any single-node isolation produces the same minority/majority asymmetry.
