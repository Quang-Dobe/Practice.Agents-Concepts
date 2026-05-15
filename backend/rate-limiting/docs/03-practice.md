# Rate Limiting — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, rate limiting is the thin layer sitting between your authentication middleware and your business logic — usually a couple of dozen lines of config in `Program.cs` plus a Redis dependency the team forgets exists until Redis hiccups. It's invisible when it works and deafening when it doesn't.

You'll meet it again at the **gateway**: Kong, Envoy, or AWS API Gateway plugins are where per-tenant SLOs live, separate from the per-endpoint business rules in the app. And once more at the **edge**: Cloudflare or AWS WAF rate-based rules that exist mainly to shed obvious abuse before it costs you origin compute.

The fourth place is sneakier — the *client-side limiter wrapped around a third-party SDK*, because OpenAI, Stripe, Twilio, and SendGrid all impose limits on *you*, and a Polly `RateLimitPolicy` or `SemaphoreSlim` around their SDK is the only way to stay under without writing retry loops everywhere.

If you work on a public API, a login endpoint, an LLM-backed feature, or anything multi-tenant, rate limiting is load-bearing. Treat it that way.

## Best practices

### 1. Key on identity, not on IP, whenever you have one
**Do:** Extract the limit key from the strongest identifier available — `sub` claim from a verified JWT, API key hash, tenant ID — and fall back to IP only for anonymous traffic.
**Why:** Office NATs, mobile carrier CGNAT, and corporate proxies aggregate thousands of legitimate users behind one IP. Keying on IP turns a legitimate burst into a customer-wide outage.
**Avoid:** Defaulting to `client_ip` everywhere because it's the easy first parameter to grab.

### 2. Make the Redis call atomic with a Lua script
**Do:** Implement check-refill-decrement as a single `EVAL`/`EVALSHA` Lua script. Use `redis-cell` (GCRA in C) if you can install modules.
**Why:** A two-step `INCR` + `EXPIRE` has a race where the `EXPIRE` is skipped if the client dies between calls, and the counter never expires. Multiply by your key cardinality and Redis memory goes to the moon.
**Avoid:** "We'll just do `INCR` and `EXPIRE` separately — it'll be fine most of the time."

### 3. Decide fail-open vs. fail-closed *explicitly* and instrument both
**Do:** Pick per-endpoint. Public read endpoints fail open (availability > correctness). Auth, payment, and LLM endpoints fail closed (cost and security > availability). Emit a metric every time the fallback fires.
**Why:** Silent fail-open during a Redis outage is how a $40,000 OpenAI bill gets generated overnight. Silent fail-closed is how a Redis blip takes the whole product down.
**Avoid:** Inheriting the library's default and never reading the docs to find out what it is.

### 4. Always set `Retry-After` and jitter it server-side
**Do:** Return `Retry-After` as a number of seconds, and add ±20% random jitter to the value so clients don't synchronize. Also emit `RateLimit-Limit`, `RateLimit-Remaining`, `RateLimit-Reset` per the IETF draft.
**Why:** Deterministic `Retry-After` produces a thundering herd at exactly `reset_time`. Your dashboard shows a clean traffic cliff followed by a near-vertical spike — and the spike re-trips the limiter.
**Avoid:** Returning `429` with no header and hoping the client implements exponential backoff.

### 5. Two-tier the limiter: local first, distributed second
**Do:** Run a small in-process token bucket (per replica, generous limit) in front of the Redis-backed global limiter. The local one absorbs hot-key bursts without a network call.
**Why:** Single-instance Redis hitting 100% CPU on Lua execution *is* a real incident — Stripe and Shopify both write about it. A local first tier cuts Redis QPS by 10–100×.
**Avoid:** Sending every single request to Redis when 95% of the answer is "obviously allowed, the bucket is full."

### 6. Dark-launch every new limit before enforcing it
**Do:** Run the limiter in "log-only" mode for a week. Compare what *would have* been blocked against complaint volume and real traffic patterns. Then flip to enforce.
**Why:** This is Stripe's published playbook. Limits set by guessing in a meeting room are almost always wrong by at least one order of magnitude, in one direction or the other.
**Avoid:** Shipping a new limit on Friday afternoon "to be safe" and finding out Monday that you blocked your biggest customer's nightly batch job.

### 7. Scope limits to (key × route-class), not just key
**Do:** Bucket routes into classes — `read`, `write`, `expensive` (search, export, AI) — and apply different limits per class. The key becomes `(tenant_id, route_class)`.
**Why:** A flat per-user limit means one heavy `/reports/export` call consumes the budget for fifty cheap `/me` polls, producing UX that feels broken for reasons the user can't see.
**Avoid:** One global "1000 req/min per user" knob applied to everything from `/healthz` to `/ai/generate`.

### 8. Cost-shape, not just count-shape, for expensive endpoints
**Do:** For LLM and similar pay-per-call backends, enforce **two** parallel limits: requests per minute (RPM) and a cost-weighted limit (TPM for tokens, dollars per hour). Charge the bucket by *estimated* cost on entry, reconcile on exit.
**Why:** A 200k-token prompt is 1000× more expensive than a 200-token one. RPM alone lets a single customer rack up a five-figure bill while staying "within limits."
**Avoid:** Counting only request volume on endpoints where per-request cost varies by orders of magnitude.

### 9. Bound the key-space cardinality
**Do:** Hash long keys to fixed-length, set Redis `maxmemory-policy allkeys-lru`, and reject or aggregate keys that explode cardinality (e.g., per-URL-path limits on a path that includes a UUID).
**Why:** Naive `(user_id, path)` keying on a path like `/orders/{id}` produces unbounded keys, and Redis OOM-kills itself overnight.
**Avoid:** Using the raw URL as part of the key without route-template normalization.

### 10. Make the limiter observable from day one
**Do:** Emit four metrics: `ratelimit.allowed{route,tier}`, `ratelimit.rejected{route,tier}`, `ratelimit.fallback{reason}`, and `ratelimit.latency_ms` (p99). Alert on rejection rate spikes *and* on fallback-mode activation.
**Why:** Without the rejection metric you'll only learn about a misconfigured limit from a support ticket. Without the fallback metric you won't know your limiter has been silently disabled for three weeks.
**Avoid:** Logging rejections to stdout and calling it observability.

## Anti-patterns to recognize

- **The IP-only limiter on an authenticated endpoint.** Looks fine in dev where everyone has a different IP; in production one shared corporate NAT triggers limits for thousands of real users. Use the authenticated identity once you have it.
- **The `INCR`-then-`EXPIRE` racer.** Two Redis calls instead of a Lua script. Under partial failure the TTL is never set and the counter leaks. Use one atomic script.
- **The fail-open default no one chose.** The library failed open, the team never noticed, and the limiter was effectively disabled for the eighteen months since the last Redis incident. Make the choice explicit and alert on it.
- **The synchronized retry stampede.** Server returns `Retry-After: 60`, ten thousand clients all retry at second 61, the limiter trips again, infinite loop. Jitter on the server, exponential backoff on the client.
- **The global single-Redis bottleneck.** All limiter traffic hits one Redis primary, which becomes the production-traffic CPU ceiling. Shard by key, or add a per-replica local first tier.
- **The unbounded key-space.** Keying by raw path or query string creates a new Redis key per unique URL, and memory grows linearly with traffic until the box dies. Normalize to route templates.
- **The "we'll add limits later" project.** Limits are retrofitted after the first incident, set conservatively to avoid breaking anyone, and then become permanent because nobody dares raise them. Dark-launch from the start, even at generous values.
- **The limiter that punishes its own retries.** Internal retry logic (Polly, gRPC's built-in retry) fires three attempts that each consume a token, so legitimate clients get rate-limited by their own resilience code. Either retry-budget separately or charge once per logical operation.

## Real-world usage patterns

**Public REST API with tiered pricing.** A B2B SaaS exposes a versioned REST API; free tier gets 60 req/min, Pro 1,000, Enterprise negotiated. Token bucket per `(api_key, route_class)` in Redis, IETF `RateLimit-*` headers on every response, dark-launched for two weeks before enforcement. *Lesson:* the headers themselves became a feature — customers built dashboards on `RateLimit-Remaining` and noticed problems before support did.

**Login / credential-stuffing defense.** An e-commerce backend rate-limits `POST /auth/login` by `(email, IP)` tuple at 5/min and by IP at 30/min. The limiter fails *closed*: a Redis blip is preferable to an open door. Pair this with a CAPTCHA challenge after N rejections rather than a hard block. *Lesson:* keying purely by email enables an attacker to lock out a target user; the tuple key prevents weaponized lockout.

**LLM gateway in front of OpenAI/Anthropic.** An internal gateway sits between application services and upstream LLM providers, enforcing both RPM and TPM per-team, charging the token bucket by estimated tokens at request time and reconciling on response. Spillover above quota gets queued (leaky bucket) rather than `429`'d, because the calling code is batch-style. *Lesson:* the queue depth metric is the leading indicator of cost overruns — when it grows steadily, someone has shipped a new feature without telling you.

**Multi-tenant background-job runner.** A scheduler runs tenant-owned jobs on shared workers. Rate limit is concurrency-based (`SemaphoreSlim(8)` per tenant), not RPM, because the resource being protected is worker slots, not API throughput. *Lesson:* "rate limiting" and "concurrency limiting" are different tools; pick by what's actually scarce.

## Operational checklist

- Monitoring: are `allowed`, `rejected`, `fallback`, and limiter p99 latency all on a dashboard with alerts on each?
- Failure handling: when Redis goes away, do you know within 60 seconds whether you're failing open or closed, and is that the choice you made on purpose?
- Atomicity: is your check-and-decrement a single Lua script or `redis-cell` call, with no `INCR`+`EXPIRE` two-step anywhere in the code path?
- Headers: do `429` responses include `Retry-After` with server-side jitter, plus the standard `RateLimit-*` triple?
- Key hygiene: are limit keys normalized to route templates, hashed if long, and bounded so Redis memory can't grow unbounded?
- Security: is the login endpoint keyed by `(email, IP)` not just email (lockout abuse) or just IP (NAT shared)?
- Cost: on every paid-per-call backend, is there a *cost-weighted* limit, not only a request-count one?
- Onboarding: can a new engineer find the limit config in under five minutes and tell you which limits are enforced vs. log-only?
- Tests: is there an integration test that simulates Redis being unreachable and asserts the documented fallback behavior?
- Client behavior: do your own internal clients (and SDKs you ship) respect `Retry-After` with jitter and exponential backoff?

## How this topic typically evolves in a codebase

Teams almost always start with an in-process middleware limiter — `AddFixedWindowLimiter` in ASP.NET, `express-rate-limit` in Node — keyed by IP, single-instance, no observability. It works for the first six months because traffic is low and the app runs as one replica.

The first migration happens at horizontal scale-out: with three replicas the effective limit is now 3× what's configured, and the team moves to Redis-backed shared state. This is usually the painful point — a Lua script gets written, atomicity bugs are discovered the hard way, and someone learns what fail-open means at 2 a.m. during a Redis failover.

The second migration is to **tiered limits** (per-tenant, per-route-class) once the product becomes multi-tenant or adds pricing tiers. This forces the limit config out of code and into a database or feature-flag system, because product, not engineering, now owns the values. The final form, reached by maybe one in five projects, is a dedicated limit service (gRPC, often `envoyproxy/ratelimit`) shared across the fleet, with adaptive limits that move with downstream health. By then rate limiting has stopped being middleware and become a platform capability with its own on-call rotation.

## Further reading

- [Scaling your API with rate limiters — Stripe Engineering](https://stripe.com/blog/rate-limiters) — the canonical production write-up; four limiter types, dark-launch methodology, and why token bucket wins.
- [`envoyproxy/ratelimit`](https://github.com/envoyproxy/ratelimit) — Lyft's open-source gRPC limit service; reading the config schema teaches you how to structure descriptors and tiers.
- [`redis-cell` (GCRA in Rust)](https://github.com/brandur/redis-cell) — a Redis module implementing the Generic Cell Rate Algorithm; the README explains why GCRA beats a hand-rolled token bucket.
- [IETF draft: `draft-ietf-httpapi-ratelimit-headers`](https://datatracker.ietf.org/doc/draft-ietf-httpapi-ratelimit-headers/) — the spec replacing the `X-RateLimit-*` zoo; implement these now and forget the vendor dialects.
- [Distributed Rate Limiting — Five Problems That Break Your Counters](https://dev.to/saumya_karnwal/distributed-rate-limiting-five-problems-that-break-your-counters-454) — a tight, scar-tissue list of the failure modes outlined above with concrete repros.
- [ASP.NET Core Rate Limiting docs (Microsoft Learn)](https://learn.microsoft.com/aspnet/core/performance/rate-limit) — the four built-in algorithms, partition keys, and how to wire a Redis store; required reading for the .NET track.
