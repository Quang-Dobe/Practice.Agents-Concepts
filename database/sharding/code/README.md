# Sharding — MVP Code

The smallest runnable demo of horizontal sharding. Tiny Clean Architecture solution, ~180 lines of code.

## What it demonstrates

- A **routing function** `f(shard_key) -> shard_id` is the only thing that makes a sharded repository different from a normal one — handlers upstream are unchanged.
- **Hash-modulo** vs **consistent hashing with virtual nodes** as two concrete routers.
- **Single-shard reads**: every `GetUserQuery` carries the shard key, so it hits exactly one shard.
- The **rebalancing penalty** going N -> N+1 shards: hash-modulo rehashes ~(N-1)/N of keys; consistent hashing moves only ~K/N. That ratio is why Cassandra/Dynamo use rings.

## Prerequisites

.NET SDK 8.0+. No external services — each "shard" is an in-memory `Dictionary<Guid, User>`.

## Run it

```bash
cd code
dotnet run --project Console
```

## Expected output (abridged)

```
=== Hash modulo (hash(key) % 4) ===
  INSERT user-01  id=01000000  -> shard 2
  SELECT id=04000000  -> shard 1, found='user-04'
  distribution: [3, 4, 3, 2]
=== Consistent hash (4 shards x 150 vnodes) ===
  distribution: [4, 2, 3, 3]
=== Rebalance: grow from 4 -> 5 shards ===
  hash-modulo moved      10 / 12 keys
  consistent-hash moved  3 / 12 keys
```

Exact shard numbers vary by hash; the rebalance ratio is the load-bearing observation.

## What to try next

- Bump `UserCount` to 10,000 and watch the distribution flatten.
- Set `vnodesPerShard: 1` and watch consistent-hash skew badly.
- Change `ShardCount` from 4 to 16 — the consistent-hash advantage widens.
- Route `GetUserQuery` by `Name` instead of `Id` — you just invented a scatter-gather query.
