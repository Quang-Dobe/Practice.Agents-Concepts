namespace Application.Abstractions;

// The routing function from 02-deep-dive.md: f(shard_key) -> shard_id.
// Keeping it as a port lets the Console swap hash-modulo for consistent-hash
// without the handlers caring.
public interface IShardRouter
{
    int RouteToShard(Guid shardKey);
    string StrategyName { get; }
}
