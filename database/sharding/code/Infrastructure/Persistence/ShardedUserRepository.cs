using Application.Abstractions;
using Domain;

namespace Infrastructure.Persistence;

// Sharded repository = N independent "databases" (here: dictionaries) plus
// the router. Reads and writes both consult the router exactly once on the
// shard key, then touch a single shard. That single-shard property is what
// makes sharded systems scale at all.
public sealed class ShardedUserRepository : IUserRepository
{
    private readonly IShardRouter _router;
    private readonly Dictionary<Guid, User>[] _shards;

    public ShardedUserRepository(IShardRouter router, int shardCount)
    {
        _router = router;
        // One in-memory dictionary per shard. In production each of these
        // would be its own MySQL/Postgres instance with its own replication.
        _shards = Enumerable.Range(0, shardCount)
            .Select(_ => new Dictionary<Guid, User>())
            .ToArray();
    }

    public Task<int> Add(User user, CancellationToken ct)
    {
        var shardId = _router.RouteToShard(user.Id);
        _shards[shardId][user.Id] = user;
        return Task.FromResult(shardId);
    }

    public Task<(User? user, int shardId)> GetById(Guid id, CancellationToken ct)
    {
        var shardId = _router.RouteToShard(id);
        _shards[shardId].TryGetValue(id, out var user);
        return Task.FromResult((user, shardId));
    }

    // Exposed for the demo's distribution histogram. Not part of IUserRepository
    // because no real application code should care about per-shard counts.
    public int CountOnShard(int shardId) => _shards[shardId].Count;
}
