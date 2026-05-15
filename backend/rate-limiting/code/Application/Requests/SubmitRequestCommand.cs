using Application.Abstractions;
using Application.Mediator;
using Domain;

namespace Application.Requests;

// The use case: "this caller wants to make one request; admit or reject it."
// The handler doesn't know which algorithm or which store backs the quota —
// that's an Infrastructure choice injected through IRequestQuotaService.
public sealed record SubmitRequestCommand(string CallerKey) : IRequest<SubmitRequestOutcome>;

// Mirrors the IETF RateLimit-* headers a real HTTP layer would emit on top of this.
public sealed record SubmitRequestOutcome(
    bool Accepted,
    double TokensRemaining,
    TimeSpan RetryAfter,
    string? Error);

public sealed class SubmitRequestHandler(IRequestQuotaService quota)
    : IRequestHandler<SubmitRequestCommand, SubmitRequestOutcome>
{
    public Task<SubmitRequestOutcome> Handle(SubmitRequestCommand cmd, CancellationToken ct)
    {
        // Single check-and-spend. In a Redis-backed implementation this same
        // call would translate to one EVALSHA round-trip executing a Lua script
        // (atomicity guarantee from 02-deep-dive.md, step 2-4).
        var decision = quota.TryAcquire(cmd.CallerKey);

        if (decision.Allowed)
        {
            // Accepted path. A real handler would now invoke the actual business
            // logic — DB write, third-party call, LLM inference. We skip that
            // because the topic *is* the admission check.
            return Task.FromResult(new SubmitRequestOutcome(
                Accepted: true,
                TokensRemaining: decision.TokensRemaining,
                RetryAfter: TimeSpan.Zero,
                Error: null));
        }

        // Rejected path. In an HTTP host this is where middleware would short-
        // circuit the pipeline with `429 Too Many Requests` and emit `Retry-After`.
        // In this console demo we surface the outcome so the driver can print it.
        // The RateLimitExceededException in Domain is the sentinel an HTTP layer
        // would throw and a global filter would translate to the 429 response.
        return Task.FromResult(new SubmitRequestOutcome(
            Accepted: false,
            TokensRemaining: decision.TokensRemaining,
            RetryAfter: decision.RetryAfter,
            Error: $"429 Too Many Requests (retry after {decision.RetryAfter.TotalMilliseconds:F0} ms)"));
    }
}
