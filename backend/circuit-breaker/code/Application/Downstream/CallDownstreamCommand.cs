using Application.Abstractions;
using Application.Mediator;
using Domain;

namespace Application.Downstream;

// The use case: "make one call to the downstream and tell me what happened."
// The handler doesn't know there's a breaker — that's an Infrastructure concern
// injected as IDownstreamService. The handler only knows the port and the
// sentinel exception type from Domain.
public sealed record CallDownstreamCommand : IRequest<CallOutcome>;

public sealed record CallOutcome(bool Succeeded, string? Payload, string? Error);

public sealed class CallDownstreamHandler(IDownstreamService downstream)
    : IRequestHandler<CallDownstreamCommand, CallOutcome>
{
    public async Task<CallOutcome> Handle(CallDownstreamCommand _, CancellationToken ct)
    {
        try
        {
            var payload = await downstream.Call(ct);
            return new CallOutcome(true, payload, null);
        }
        catch (CircuitOpenException ex)
        {
            // The fallback path — the deep-dive's "fail fast" branch. In a real
            // app this is where you'd return a cached value, default object,
            // or degraded response. Here we just surface that we short-circuited.
            return new CallOutcome(false, null, $"SHORT-CIRCUITED: {ex.Message}");
        }
        catch (Exception ex)
        {
            // A real downstream failure (timeout, 5xx, network blip). The breaker
            // has already counted this; we just report it to the driver.
            return new CallOutcome(false, null, $"FAILED: {ex.Message}");
        }
    }
}
