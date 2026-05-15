namespace Domain;

// The outcome of one admission check, mirroring the IETF RateLimit-* header
// triple from 02-deep-dive.md: was it allowed, how many tokens remain, and
// when (if denied) is it safe to try again. Pure data — no behavior, no IO.
public sealed record RateLimitDecision(
    bool Allowed,
    double TokensRemaining,
    TimeSpan RetryAfter);
