using Application.Abstractions;
using Application.Mediator;
using Domain.Tokens;

namespace Application.Tokens;

public sealed record IssueTokenCommand(string UserId, string Role)
    : IRequest<AccessToken>;

public sealed class IssueTokenHandler(ITokenIssuer issuer)
    : IRequestHandler<IssueTokenCommand, AccessToken>
{
    public Task<AccessToken> Handle(IssueTokenCommand cmd, CancellationToken ct) =>
        Task.FromResult(issuer.Issue(new TokenSubject(cmd.UserId, cmd.Role)));
}
