using System.Collections.Concurrent;
using Application.Abstractions;
using Domain;

namespace Infrastructure.RateLimiting;

// The Infrastructure adapter that satisfies the Application port. It keeps one
// TokenBucket per caller key — the same shape a Redis-backed implementation
// would have, just with a ConcurrentDictionary in place of a Redis hash.
//
// Key-space hygiene (practice doc, best practice #9) is the caller's job here:
// in production you would hash long keys to a fixed length, set an LRU policy
// on the store, and normalize URL paths to route templates before keying.
//
// Buckets are created lazily on first use. Each one starts full, which matches
// the deep-dive's "first burst is free" property of token bucket.
public sealed class TokenBucketQuotaService(double capacity, double refillPerSecond)
    : IRequestQuotaService
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();

    public RateLimitDecision TryAcquire(string key)
    {
        // GetOrAdd is atomic for the dictionary lookup; the bucket itself has
        // its own internal lock around the check-and-spend.
        var bucket = _buckets.GetOrAdd(key, _ => new TokenBucket(capacity, refillPerSecond));
        return bucket.TryAcquire();
    }
}
