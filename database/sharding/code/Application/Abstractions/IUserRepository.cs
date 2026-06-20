using Domain;

namespace Application.Abstractions;

// The handler only ever sees this one repository interface. The fact that
// it transparently fans out to N physical shards lives entirely in the
// Infrastructure adapter — that is the whole point of the routing layer.
public interface IUserRepository
{
    // Returns the shard index the row landed on, purely so the demo can print it.
    Task<int> Add(User user, CancellationToken ct);
    Task<(User? user, int shardId)> GetById(Guid id, CancellationToken ct);
}
