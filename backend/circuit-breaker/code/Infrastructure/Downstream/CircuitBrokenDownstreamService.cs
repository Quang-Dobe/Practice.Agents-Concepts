using Application.Abstractions;
using Infrastructure.Resilience;

namespace Infrastructure.Downstream;

// Decorator: wraps the real downstream with the breaker. The Application layer
// gets a plain IDownstreamService and never learns the breaker exists — exactly
// the seam Clean Architecture is designed to give us.
public sealed class CircuitBrokenDownstreamService(
    IDownstreamService inner,
    CircuitBreaker breaker) : IDownstreamService
{
    public Task<string> Call(CancellationToken ct) =>
        breaker.Execute(inner.Call, ct);
}
