# Rate Limiting — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
Rate limiting is the enforcement of an upper bound on the **rate of accepted requests** from an identifiable caller (an IP, a user, an API key, a tenant, a route) over a finite time window, by **counting** events against shared state and **rejecting or shaping** any event that would push the count past a configured threshold. Formally, given a stream of events `e_1, e_2, …` arriving at times `t_i`, the limiter decides `allow(e_i) ∈ {true, false}` such that for every sliding interval of length `W`, the number of allowed events for a given key never exceeds `N`.

It is a special case of **admission control**. Where a circuit breaker reacts to *downstream* health and a load shedder reacts to *local* resource pressure, a rate limiter reacts to *caller behavior*.

### The core building blocks
- **Key extraction.** Pulling the identifier the limit is scoped to — `X-API-Key`, `sub` claim from a JWT, `client_ip` after trusting `X-Forwarded-For` for *N* hops, or a tuple like `(tenant_id, route_template)`.
- **Counter store.** Where the per-key state lives: an in-memory dictionary, a Redis hash, a sorted set, or a CRDT-backed distributed counter.
- **Algorithm.** The policy that turns (current state, now, request) into allow/deny: fixed window, sliding window log, sliding window counter, leaky bucket, token bucket, or GCRA (Generic Cell Rate Algorithm).
- **Decision plane.** The code path that either accepts the request and updates state, or returns `429 Too Many Requests` with a `Retry-After` header.
- **Policy configuration.** Per-route or per-tier limits, usually loaded from config (Envoy YAML, ASP.NET Core `AddFixedWindowLimiter`, Nginx `limit_req_zone`).

### How it relates to the broader landscape
Rate limiting sits in the family of **traffic management** primitives alongside load shedding, prioritization, queueing, and admission control. Siblings: **quotas** (longer horizon, billing-shaped), **concurrency limiters** (cap in-flight rather than per-second), **bulkheads** (isolate failure domains), and **circuit breakers** (open on failure, not on volume). All five overlap in practice but solve subtly different problems — a token bucket cannot save you from a flapping downstream, and a circuit breaker cannot stop a credential-stuffing attack.

## Where

### Where it runs / lives in the stack
A request can be rate-limited at up to four layers, often simultaneously:

1. **Edge / CDN** — Cloudflare, Fastly, AWS WAF. Cheapest to enforce, terminates abuse before it touches your origin. Coarse keys (IP, ASN, country).
2. **API gateway / service mesh** — Kong, Apigee, Envoy + Lyft `ratelimit` service. Enforced after TLS termination but before app code. Knows routes and auth claims; can call out to a global gRPC limit service.
3. **Application middleware** — ASP.NET Core `RateLimiter` middleware, Express `express-rate-limit`, Django Ratelimit. Knows business-level identifiers (user ID, plan tier).
4. **Downstream resource wrappers** — a `SemaphoreSlim` or Polly `RateLimitPolicy` around a third-party SDK to respect *their* limits.

A mature system layers them: edge stops obvious abuse, gateway enforces per-tenant SLO, app enforces per-route business rules.

### Where you typically encounter it
- **Stripe** publishes per-account read/write limits enforced by a token-bucket implementation backed by Redis.
- **GitHub REST API** — 5,000 authenticated requests/hour, surfaced via `X-RateLimit-*` headers.
- **AWS APIs** — each service has its own per-region per-account TPS limit, enforced by a token bucket inside the gateway.
- **Cloudflare Rate Limiting Rules** — counted at the edge, configurable on any request attribute (header, cookie, JSON body field).
- **OpenAI / Anthropic APIs** — both RPM (requests per minute) and TPM (tokens per minute) limits, the second being a cost-aware variant.
- **Envoy + `envoyproxy/ratelimit`** — the de facto OSS implementation for service-mesh global limits, originally open-sourced by Lyft.

### Ecosystem and tooling
- **For .NET apps:** `Microsoft.AspNetCore.RateLimiting` (built into ASP.NET Core since 7, four algorithms shipped). For distributed state, pair with `StackExchange.Redis` and a Lua script.
- **For Node/Express:** `express-rate-limit`, `rate-limiter-flexible` (the latter supports Redis, Memcached, Mongo, and clustered Node).
- **For service mesh / gateway:** Envoy's `ratelimit_filter` + `envoyproxy/ratelimit` gRPC service, Kong's `rate-limiting-advanced` plugin, NGINX `limit_req_zone`.
- **For edge:** Cloudflare Rate Limiting Rules, AWS WAF rate-based rules, Fastly VCL `ratecounter`.
- **For Redis-based custom limiters:** `redis-cell` module (GCRA in C), `RedisRateLimiting` NuGet package, Stripe's open-source `ratelimit` Lua scripts.
- **For client-side compliance:** Polly (.NET), `tenacity` (Python), `retry-axios` (Node) — all handle `Retry-After` parsing and exponential backoff with jitter.

## When

### When the topic emerged and why
The pattern predates HTTP. The **Generic Cell Rate Algorithm (GCRA)** was specified by the ATM Forum in 1996 as a leaky-bucket variant for cell-switched networks. **Token bucket** appeared in network QoS literature in the late 1980s. Web-scale rate limiting became a daily concern around 2006–2010 when public APIs (Twitter, Flickr, AWS S3) discovered that a single buggy client could trivially exhaust capacity. The `429` status code was standardized in **RFC 6585 (2012)**. The current IETF draft `draft-ietf-httpapi-ratelimit-headers` (at revision 10 in 2026) is trying to standardize the `RateLimit-Limit`, `RateLimit-Remaining`, and `RateLimit-Reset` headers, replacing the long-standing zoo of vendor-prefixed `X-RateLimit-*` variants.

### When to use it in a project
Reach for it when:
- The endpoint is reachable by **untrusted or partially trusted** callers.
- A single caller's pathological behavior could **degrade other callers' experience** (shared DB, shared queue, shared cache).
- You need **per-tier differentiation** (free vs. paid).
- An endpoint has **expensive backing cost** (LLM inference, geocoding, transactional email).
- You depend on a **third-party API with its own limits** and need to stay below them.

### When NOT to use it
Avoid it when:
- Traffic is **purely east-west, mTLS-authenticated, in a trusted mesh** — use timeouts, concurrency caps, and load shedding instead.
- The real problem is **DDoS volume at L3/L4** — that needs a scrubbing service, not an application limiter that will itself be saturated.
- Fairness must be **strict and queue-like** with FIFO ordering — use an explicit queue.
- Calls are **batched and asynchronous** with no SLA on per-call latency — a queue-based throughput controller is clearer.

## How

### How it works under the hood
Walk through a single request hitting an app-level limiter backed by Redis, using the **token bucket** algorithm:

1. **Key extraction.** Middleware reads `Authorization: Bearer …`, validates the JWT, takes `sub` as the limit key. Falls back to client IP for anonymous requests.
2. **Atomic check-and-decrement.** The middleware invokes a Lua script via `EVALSHA` on Redis. The script reads two fields from a hash: `tokens` (current count) and `last_refill` (epoch ms).
3. **Refill.** The script computes `elapsed = now - last_refill`, then `new_tokens = min(capacity, tokens + elapsed * refill_rate)`.
4. **Decision.** If `new_tokens >= 1`, decrement by 1, write back, return `(allowed=1, remaining=new_tokens-1)`. Otherwise write back unchanged, return `(allowed=0, retry_after_ms=ceil((1 - new_tokens) / refill_rate))`.
5. **TTL.** Script sets `PEXPIRE` to `capacity / refill_rate` so idle keys evict.
6. **Response shaping.** On `allowed=0` the middleware short-circuits the pipeline, emits `429`, sets `Retry-After: <seconds>`, `RateLimit-Limit`, `RateLimit-Remaining: 0`, `RateLimit-Reset`. Logs and increments a metric `ratelimit.rejected{route, tier}`.

The whole sequence is a single round-trip to Redis. Atomicity is provided by Redis's single-threaded command loop executing the Lua script as one indivisible unit — without that guarantee, the `INCR`-then-`EXPIRE` race condition leaks counters indefinitely.

For a **sliding window log** the storage is a Redis sorted set keyed by user; each request adds a member with `score = now`. The script then `ZREMRANGEBYSCORE 0 (now - W)` to evict, `ZCARD` to count, and rejects if the count exceeds `N`. This costs O(N) memory per key. The **sliding window counter** approximation keeps just two integers — the count in the current fixed window and the previous one — and estimates `count_prev * overlap_fraction + count_curr`. Accuracy within a few percent at O(1) memory.

### Key trade-offs

| Choice | You gain | You give up |
|---|---|---|
| Token bucket | Burst tolerance, simple math, well-understood | Two parameters to tune (capacity + rate) instead of one |
| Leaky bucket (shaping) | Perfectly smooth output rate | Adds queueing latency; needs a queue |
| Fixed window | One integer per key, trivial to reason about | Up to 2× burst at window boundaries |
| Sliding window log | Exact accuracy | O(N) memory, expensive on hot keys |
| Sliding window counter | O(1) memory, ~accuracy | ~3–5% error vs. exact |
| Local (in-process) limiter | Sub-microsecond, no network | Per-instance limits multiply by replica count |
| Distributed (Redis) limiter | Globally correct | Adds 0.5–2 ms latency; Redis is now in your hot path |
| Edge limiter | Stops abuse cheaply, far from origin | Doesn't know app-level identities (user, plan) |
| Reject (`429`) | Immediate, no resource use | Pushes complexity to clients |
| Shape (queue) | Smooth UX, no client retries | Memory pressure under sustained overload |

### Common failure modes
- **Thundering herd on `Retry-After`.** All clients receive the same reset time and retry simultaneously. *Cause:* server-suggested deterministic retry without jitter on the client.
- **Counter-leak race.** Counter incremented but TTL never set; key lives forever. *Cause:* two-call `INCR`+`EXPIRE` instead of a Lua script.
- **Edge-of-window double burst.** A client sends 2N requests across a window boundary. *Cause:* fixed window with no smoothing.
- **NAT / shared IP punishment.** One key (an office NAT, a corporate proxy) sees thousands of legitimate users. *Cause:* keying by IP when authenticated identity is available.
- **Limiter as the bottleneck.** Redis hits 100% CPU on Lua script execution. *Cause:* single-instance Redis serving all keys; remediation is hash-slot sharding or local first-tier limiter.
- **Clock skew across nodes.** Sliding window log evicts wrong entries. *Cause:* using local wall clock instead of a server-side `TIME` call.
- **Silent failure under Redis outage.** Limiter fails open and all traffic is admitted. *Cause:* no explicit `fail_closed` policy on cache miss/timeout.
- **Retry storm amplification.** Clients receive `429`, retry immediately, get `429`, retry. *Cause:* aggressive client retry budget interacting with a too-tight limit.

## Why

### Why it exists
Every shared resource — CPU, DB connections, third-party API quota, dollars per inference call — is finite. Without admission control, the system's behavior under overload is **unspecified**: typically queue growth, latency blowup, and cascading timeouts. Rate limiting collapses an infinite-dimensional failure space into a single explicit contract: *up to N per W, beyond that a `429`.* The contract is cheap to enforce, cheap to communicate, and gives clients deterministic feedback.

### Why it looks the way it does
Token bucket dominates because it captures the only two facts a real API cares about: **steady-state rate** (the drip) and **tolerable burst** (the bucket size). Pure leaky bucket forces queueing — the API must hold requests and reply slowly — which complicates HTTP semantics and adds latency. Pure fixed window is too crude. Sliding window log is too memory-hungry at scale. Token bucket is the Goldilocks point: O(1) state, two intuitive knobs, allows the bursts real clients actually produce on cold start, and degrades gracefully into a flat rate at sustained load.

The `429` + `Retry-After` design instead of, say, a silent TCP RST exists because **HTTP is request-response and clients need to know why**. Failing open vs. failing closed during cache outage is a deliberate choice: most public APIs fail open (availability > strict correctness) while billing-sensitive systems (LLM APIs) fail closed (cost > availability).

### Why it matters now
In 2026 three pressures keep rate limiting front-and-center. First, **LLM API costs** are real money per call, so token-aware rate limits (RPM + TPM) have become a first-class product surface. Second, **agentic clients** — AI agents looping with retries — generate traffic patterns that humans never did, and naive limits either let them runaway or strangle legitimate automation; adaptive, identity-aware limits are the new baseline. Third, **multi-tenant SaaS** has matured to the point that per-tenant fairness during noisy-neighbor incidents is a contractual SLA, not a nice-to-have. The IETF `RateLimit` headers draft is on track to become an RFC, which will finally end the `X-RateLimit-*` dialect war.

## Open questions / things to verify in practice
- Under a Redis blip, does my limiter fail open or fail closed? What does the metric for that look like?
- How much added p99 latency does a Redis-backed limiter cost on the actual hot path, measured end-to-end?
- Does my chosen algorithm cope with the bursty cold-start pattern of mobile apps that fire 30 requests on launch?
- Are my `Retry-After` values jittered, or am I creating a synchronized retry stampede?
- When two layers of limiter disagree (edge says ok, app says no), is the user-visible error coherent?
- What's the cardinality of my limit-key space, and what's the resulting Redis memory ceiling?
