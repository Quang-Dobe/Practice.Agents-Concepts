using Application.Abstractions;
using System.Security.Cryptography;

namespace Infrastructure.Sharding;

// The classic textbook router: shard = hash(key) % N.
// Even distribution, but if N changes, (N-1)/N of the keys move — exactly
// the rebalancing pain 02-deep-dive.md warns about.
public sealed class HashModuloRouter(int shardCount) : IShardRouter
{
    public string StrategyName => $"hash(key) % {shardCount}";

    public int RouteToShard(Guid shardKey)
    {
        // SHA-256 over the Guid bytes gives us a stable, well-distributed hash.
        // GetHashCode would also work but is process-local; SHA is reproducible
        // across restarts, which matches what a real cluster needs.
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(shardKey.ToByteArray(), hash);

        // Take the first 4 bytes as an unsigned int, mod N. Mask off the sign
        // bit instead of using BitConverter -> int so negative ints can't sneak
        // a negative shard index past the modulo.
        var bucket = BitConverter.ToUInt32(hash[..4]) % (uint)shardCount;
        return (int)bucket;
    }
}
