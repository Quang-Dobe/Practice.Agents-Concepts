using Application.Abstractions;
using Application.Mediator;
using Domain;

namespace Application.Users;

// Mutates state -> Command. Returns the chosen shard id so the caller can
// observe routing decisions directly.
public sealed record AddUserCommand(Guid Id, string Name) : IRequest<int>;

public sealed class AddUserHandler(IUserRepository repo)
    : IRequestHandler<AddUserCommand, int>
{
    public Task<int> Handle(AddUserCommand cmd, CancellationToken ct)
        => repo.Add(new User(cmd.Id, cmd.Name), ct);
}
