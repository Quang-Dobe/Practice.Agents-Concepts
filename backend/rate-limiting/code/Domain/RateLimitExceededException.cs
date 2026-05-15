namespace Domain;

// Sentinel thrown when the bucket is empty and a caller asks for another token.
// Carries RetryAfter so the upstream HTTP layer can emit a `Retry-After` header
// and the `RateLimit-Reset` value without recomputing the math.
public sealed class RateLimitExceededException(string key, TimeSpan retryAfter)
    : Exception($"Rate limit exceeded for '{key}'. Retry after {retryAfter.TotalMilliseconds:F0} ms.")
{
    public string Key { get; } = key;
    public TimeSpan RetryAfter { get; } = retryAfter;
}
