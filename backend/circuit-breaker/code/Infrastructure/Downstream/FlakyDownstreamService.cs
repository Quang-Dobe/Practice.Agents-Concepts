using Application.Abstractions;

namespace Infrastructure.Downstream;

// The "remote service" we're protecting. Deterministic timeline so the demo
// output is reproducible:
//
//   First 3 successful calls   : healthy warm-up (gives the breaker Closed samples).
//   Next ~SickDuration of time : every call throws (simulates an outage).
//   After that                 : healthy again (simulates upstream recovery).
//
// Recovery is wall-clock based — not call-count based — so the breaker's
// Half-Open probe lands on a "recovered" downstream after the simulated outage
// window passes, regardless of how many calls were short-circuited in between.
public sealed class FlakyDownstreamService : IDownstreamService
{
    private static readonly TimeSpan SickDuration = TimeSpan.FromMilliseconds(1500);

    private int _healthySamplesServed;
    private DateTimeOffset? _sickSince;

    public Task<string> Call(CancellationToken ct)
    {
        // Phase 1: serve a few clean responses to seed the breaker's Closed window.
        if (_healthySamplesServed < 3)
        {
            _healthySamplesServed++;
            return Task.FromResult($"ok (warm-up #{_healthySamplesServed})");
        }

        // Phase 2: simulate an outage starting the moment we leave warm-up.
        _sickSince ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - _sickSince < SickDuration)
            throw new InvalidOperationException("downstream 503");

        // Phase 3: recovered. Subsequent calls succeed.
        return Task.FromResult("ok (recovered)");
    }
}
