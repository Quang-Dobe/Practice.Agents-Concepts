# Vector Search — MVP Code

The smallest runnable demo of vector search: exact k-NN, an IVF index, and filtered search. About 60 lines of standard-library Python, comments excluded.

## What it demonstrates
- Exact k-NN as a flat scan, used as brute-force ground truth for recall@k (see `../docs/02-deep-dive.md § What`).
- An IVF index built from scratch: k-means trained on a sample, one inverted list per centroid, and a query that scans only the `nprobe` nearest cells.
- The recall/work trade-off: each `nprobe` value prints recall@10 next to the share of the corpus it scanned.
- The post-filter failure mode: a 10%-selective filter over 40 ANN candidates returns about 4 rows instead of 10, while pre-filter plus exact scan returns all 10 (`02-deep-dive.md § Common failure modes`).

## Prerequisites
- Python 3.11+. No packages to install.

## Run it

```bash
python mvp.py
```

## Expected output
Runs in about 2 seconds. With `random.seed(7)`:

```
nprobe  recall@10  vectors scanned per query
     1      0.382     104  (  2% of corpus)
     4      0.716     406  (  8% of corpus)
    16      0.962    1599  ( 32% of corpus)
    50      1.000    5000  (100% of corpus)
  post-filter (ANN top-40, then WHERE): 4.1 rows on average
  pre-filter  (WHERE, then exact scan): 10.0 rows on average
```

At `nprobe=1` (pgvector's `ivfflat.probes` default) recall is poor. Scanning every cell (`nprobe=50`) is just a slower flat scan.

## What to try next
- Change the noise in `fake_embedding` from `1.0` to `0.3` and watch recall at `nprobe=1` jump: tighter clusters mean fewer neighbours sit across cell borders.
- Replace `fake_embedding` with uniform random vectors (`normalize([random.gauss(0, 1) for _ in range(D)])`) and watch recall drop at every `nprobe`. ANN relies on clustered data.
- Set `NLIST = 10`. Each cell holds more vectors, so recall rises and each query scans a larger share of the corpus.
- Change `TENANTS` to `100` (1% filter) and watch the post-filter row count fall toward zero.
