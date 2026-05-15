using Domain;

namespace Application.Abstractions;

// The admission-control port. Application code asks "may this caller make one
// more request?" without knowing whether the answer comes from an in-memory
// token bucket, a Redis Lua script, or an Envoy-side gRPC limit service.
// Keying by an opaque string keeps the deep-dive's "key on identity, not IP"
// rule entirely in the caller's hands.
public interface IRequestQuotaService
{
    RateLimitDecision TryAcquire(string key);
}
