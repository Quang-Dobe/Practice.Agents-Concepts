using Domain;

namespace Infrastructure.RateLimiting;

// Hand-rolled token bucket — the Goldilocks algorithm from 02-deep-dive.md "Why":
// O(1) state, two intuitive knobs (capacity + refillPerSecond), bursts allowed
// up to capacity, sustained load throttled to the refill rate.
//
// Two pieces of state per key:
//   _tokens     : current fractional token count (fractional so refill is lossless)
//   _lastRefill : the moment we last computed the bucket
//
// On every call we lazily refill: tokens += elapsed * rate (capped at capacity),
// then try to spend one. This is exactly the math the Redis Lua script in step
// 2-4 of the deep-dive performs — only the storage is different.
//
// Concurrency: one lock per bucket. Demo-grade. Production replaces this with
// either a Redis EVAL (single-threaded server gives atomicity for free) or
// Interlocked.CompareExchange on packed-double state.
public sealed class TokenBucket(double capacity, double refillPerSecond)
{
    private readonly object _gate = new();
    private double _tokens = capacity;             // bucket starts full — first burst is free.
    private DateTimeOffset _lastRefill = DateTimeOffset.UtcNow;

    public RateLimitDecision TryAcquire()
    {
        lock (_gate)
        {
            // ----- 1. Lazy refill: pour tokens in based on wall-clock elapsed. -----
            // Lazy beats a background timer because it costs zero when idle and
            // never produces a thundering-herd refill tick across many buckets.
            var now = DateTimeOffset.UtcNow;
            var elapsed = (now - _lastRefill).TotalSeconds;
            _tokens = Math.Min(capacity, _tokens + elapsed * refillPerSecond);
            _lastRefill = now;

            // ----- 2. Try to spend one. -----
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                // RetryAfter is meaningless on the accepted path — keep it zero.
                return new RateLimitDecision(Allowed: true, TokensRemaining: _tokens, RetryAfter: TimeSpan.Zero);
            }

            // ----- 3. Rejected — compute when one full token will be available. -----
            // This is what the HTTP layer maps to the `Retry-After` header value.
            // Real systems jitter this server-side (practice doc, best practice #4)
            // to break synchronized client retries; omitted here for deterministic output.
            var deficit = 1.0 - _tokens;
            var retryAfter = TimeSpan.FromSeconds(deficit / refillPerSecond);
            return new RateLimitDecision(Allowed: false, TokensRemaining: _tokens, RetryAfter: retryAfter);
        }
    }
}
