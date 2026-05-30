using Application.Abstractions;
using Application.Mediator;

namespace Application.Tokens;

public sealed record VerifyTokenQuery(string Token)
    : IRequest<VerificationResult>;

public sealed class VerifyTokenHandler(ITokenVerifier verifier)
    : IRequestHandler<VerifyTokenQuery, VerificationResult>
{
    public Task<VerificationResult> Handle(VerifyTokenQuery query, CancellationToken ct) =>
        Task.FromResult(verifier.Verify(query.Token));
}
