using Application.Abstractions;
using Application.Mediator;
using Domain;

namespace Application.Users;

// Single-shard read: the predicate carries the shard key (Id), so the
// router can pin the query to exactly one shard. That is the >95%-with-key
// rule from 03-practice.md in action.
public sealed record GetUserQuery(Guid Id) : IRequest<GetUserResult>;

public sealed record GetUserResult(User? User, int ShardId);

public sealed class GetUserHandler(IUserRepository repo)
    : IRequestHandler<GetUserQuery, GetUserResult>
{
    public async Task<GetUserResult> Handle(GetUserQuery query, CancellationToken ct)
    {
        var (user, shardId) = await repo.GetById(query.Id, ct);
        return new GetUserResult(user, shardId);
    }
}
