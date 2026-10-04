"""
Vector search in pure standard-library Python (no numpy, no FAISS).

What this file proves (see ../docs/02-deep-dive.md):
  1. Exact k-NN is a flat scan: score the query against every vector, keep the top k.
  2. An IVF index (k-means cells + `nprobe`) scans only a fraction of the corpus.
     It trades recall@k for less work, and `nprobe` moves you along that curve.
  3. Post-filtering an ANN candidate list quietly returns fewer than k rows.
     Pre-filtering and then scanning the matching set exactly does not.
"""
import heapq
import math
import operator
import random

random.seed(7)
N, D, K = 5_000, 16, 10   # corpus size, dimensions, top-k
NLIST = 50                # IVF cells; pgvector's rule of thumb is rows/1000 -- we use more to make cells visible
TENANTS = 10              # a tenant filter that matches 10% of rows


def normalize(v):
    # Unit length makes dot product == cosine similarity, with the same ranking as L2.
    n = math.sqrt(sum(x * x for x in v))
    return [x / n for x in v]


def dot(a, b):
    return sum(map(operator.mul, a, b))


def fake_embedding(topics):
    # Real embeddings are clustered (low intrinsic dimension). Uniform noise would be
    # the worst case for every ANN index, so fake the structure with hidden "topics".
    centre = random.choice(topics)
    return normalize([x + random.gauss(0, 1.0) for x in centre])


topics = [[random.gauss(0, 1) for _ in range(D)] for _ in range(200)]
vectors = [fake_embedding(topics) for _ in range(N)]
tenant = [random.randrange(TENANTS) for _ in range(N)]
queries = [fake_embedding(topics) for _ in range(50)]


# ---- 1. Exact k-NN: the flat scan. O(N*d) per query, and the ground truth for recall.
def flat_search(q, ids, k=K):
    return heapq.nlargest(k, ids, key=lambda i: dot(q, vectors[i]))


# ---- 2. Build an IVF index: train k-means centroids, then one inverted list per centroid.
def nearest_cells(v, n):
    return heapq.nlargest(n, range(NLIST), key=lambda c: dot(v, centroids[c]))


# Train on a sample of real data -- an IVF index trained on an empty or tiny table is useless.
sample = random.sample(vectors, 1_000)
centroids = sample[:NLIST]
for _ in range(10):  # a few Lloyd iterations is enough for a demo
    members = [[] for _ in range(NLIST)]
    for v in sample:
        members[nearest_cells(v, 1)[0]].append(v)
    centroids = [normalize([sum(col) for col in zip(*m)]) if m else centroids[c]
                 for c, m in enumerate(members)]

inverted_lists = [[] for _ in range(NLIST)]
for i, v in enumerate(vectors):
    inverted_lists[nearest_cells(v, 1)[0]].append(i)


def ivf_search(q, nprobe, k=K):
    # The whole trick: only the `nprobe` cells nearest the query are scanned.
    # A true neighbour sitting just across a cell border is simply never seen.
    candidates = [i for c in nearest_cells(q, nprobe) for i in inverted_lists[c]]
    return flat_search(q, candidates, k), len(candidates)


# ---- 3. Recall@k vs work done. Brute force gives the truth; the index is graded against it.
truth = [set(flat_search(q, range(N))) for q in queries]
print(f"N={N}  d={D}  nlist={NLIST}  k={K}  queries={len(queries)}\n")
print("nprobe  recall@10  vectors scanned per query")
for nprobe in (1, 2, 4, 8, 16, NLIST):
    hits = scanned = 0
    for q, t in zip(queries, truth):
        found, n = ivf_search(q, nprobe)
        hits += len(t & set(found))
        scanned += n
    avg = scanned / len(queries)
    print(f"{nprobe:>6}  {hits / (K * len(queries)):>9.3f}  {avg:>6.0f}  ({avg / N:>4.0%} of corpus)")

# ---- 4. Filtering. Post-filter: fetch 40 ANN candidates (like ef_search=40), then drop
# rows from other tenants. Pre-filter: compute the tenant's rows, then scan them exactly.
post_rows = pre_rows = 0
for q in queries:
    want = random.randrange(TENANTS)
    candidates, _ = ivf_search(q, nprobe=4, k=40)
    post_rows += len([i for i in candidates if tenant[i] == want][:K])
    pre_rows += len(flat_search(q, [i for i in range(N) if tenant[i] == want]))

print(f"\nFiltered query, tenant filter matches {1 / TENANTS:.0%} of rows, asked for k={K}:")
print(f"  post-filter (ANN top-40, then WHERE): {post_rows / len(queries):.1f} rows on average")
print(f"  pre-filter  (WHERE, then exact scan): {pre_rows / len(queries):.1f} rows on average")
