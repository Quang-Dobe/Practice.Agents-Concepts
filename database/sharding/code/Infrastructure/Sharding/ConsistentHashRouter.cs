using Application.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.Sharding;

// Consistent hashing with virtual nodes, the algorithm Cassandra/Dynamo use.
// Each physical shard owns many positions on a 32-bit ring; a key lands on
// the first vnode clockwise from its own hash. Adding shard N+1 moves only
// ~K/N of the keys instead of the (N-1)/N that plain modulo would move.
public sealed class ConsistentHashRouter : IShardRouter
{
    private readonly int _shardCount;
    private readonly int _vnodesPerShard;

    // Sorted ring: hash position -> owning shard id.
    // SortedList gives us O(log n) binary search via Keys, which is all we need.
    private readonly SortedList<uint, int> _ring = new();

    public ConsistentHashRouter(int shardCount, int vnodesPerShard = 150)
    {
        _shardCount = shardCount;
        _vnodesPerShard = vnodesPerShard;

        // Sprinkle each shard at `vnodesPerShard` pseudo-random positions on
        // the ring. More vnodes = smoother distribution; 150 is the Cassandra
        // default for the same reason — it keeps stdev across shards low.
        for (var shardId = 0; shardId < shardCount; shardId++)
            for (var v = 0; v < vnodesPerShard; v++)
                _ring[Hash($"shard-{shardId}-vnode-{v}")] = shardId;
    }

    public string StrategyName =>
        $"consistent-hash, {_shardCount} shards x {_vnodesPerShard} vnodes";

    public int RouteToShard(Guid shardKey)
    {
        var position = Hash(shardKey.ToString());

        // Walk the ring clockwise: first vnode whose position >= ours owns
        // the key. If we fall past the end, wrap around to the first vnode.
        var keys = _ring.Keys;
        var idx = BinarySearchFirstGreaterOrEqual(keys, position);
        if (idx == keys.Count) idx = 0;

        return _ring.Values[idx];
    }

    private static uint Hash(string s)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(s), hash);
        return BitConverter.ToUInt32(hash[..4]);
    }

    private static int BinarySearchFirstGreaterOrEqual(IList<uint> sorted, uint target)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (sorted[mid] < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
