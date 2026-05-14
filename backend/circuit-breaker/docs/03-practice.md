# Circuit Breaker — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, the circuit breaker is the thing sitting between your `HttpClient` (or gRPC stub) and every third-party API you can't trust: the payment gateway, the SMS provider, the identity provider, the LLM endpoint. It is invisible until the day one of them gets slow — then it is the single component that decides whether your checkout page degrades gracefully or stops responding for thirty minutes.

In a microservice architecture, you'll meet it twice. Once as an in-process library (Polly, resilience4j) on the *client* side of every outbound call, and once as a configuration knob on your service mesh (Envoy outlier detection, Istio `DestinationRule`) covering the calls you forgot to wrap. The two coexist; they read different signals and protect against different failure modes.

In data-tier code, breakers show up around databases and caches that can hang. Postgres failover taking 90 seconds, a Redis cluster repartitioning, an Elasticsearch node GC-pausing — all of these benefit from a fast-fail wrapper.

In LLM-powered features (now most features), the breaker is doing real work daily. Inference endpoints have wide tail latency, frequent partial outages, and rate-limit storms; the breaker is what stops one bad provider from holding every web worker hostage.

## Best practices

### 1. One breaker per downstream dependency, not per call site
**Do:** Keep one breaker instance per logical dependency (e.g. `payments-api`, `geocoder-api`) and inject it everywhere code calls that dependency. In Polly v8, this means one named `ResiliencePipeline` registered with `IHttpClientFactory` per upstream.
**Why:** Health is a property of the *callee*, not the call site. Per-call-site breakers fragment state — ten endpoints calling Payments each learn it's down independently, ten times.
**Avoid:** Constructing a `new CircuitBreaker(...)` inside a handler. State resets on every request and the breaker never trips.

### 2. Pick the granularity deliberately: per-service in-process, per-host at the mesh
**Do:** Use a per-service breaker in your application code (resilience4j / Polly), and let Envoy outlier detection eject individual sick replicas at the sidecar.
**Why:** A per-service breaker treats one bad replica as a global outage; a per-host breaker treats a whole-service outage as ten independent ejections that flap. Each tool covers what the other can't.
**Avoid:** Trying to implement per-host breaking in application code. You'll reinvent a load balancer badly.

### 3. Choose error-rate thresholds, not consecutive counts, above toy scale
**Do:** Use a sliding-window failure-ratio threshold (e.g. 50% over the last 100 calls or 30 seconds) with a non-trivial `minimumThroughput` (20–100 depending on traffic).
**Why:** Consecutive-failure counts trip on bursty traffic and miss slow degradations where 40% of calls fail forever. Ratios with a sample floor are stable under both spikes and lulls.
**Avoid:** "Trip after 5 failures in a row." This is the Hystrix-era default and it's wrong for almost every production service.

### 4. Tune thresholds to your traffic shape, then leave them alone
**Do:** Start at 50% failure ratio, `minimumThroughput` set so the breaker sees at least one full sample per minute under normal load, and a break duration of 15–60 seconds. Watch state-transition metrics for a week. Adjust once.
**Why:** Too tight (10% failure, throughput 5) causes flapping on routine 502s and your team learns to ignore the alerts. Too loose (90% failure, throughput 1000) means the breaker never trips before the thread pool is gone.
**Avoid:** Copying defaults from a blog post. Polly's defaults (10% failure, 100 throughput, 5s break) and resilience4j's (50%, 100, 60s) are deliberately conservative starting points, not production values.

### 5. Always include slow-call detection
**Do:** Treat calls exceeding a latency threshold as failures (`slowCallDurationThreshold` in resilience4j, custom predicate in Polly). Set the threshold at roughly your p99 + headroom, not your timeout.
**Why:** The failure mode that actually kills you is not 500s — it's calls that succeed in 28 seconds when they normally take 80ms. Pure error-rate breakers happily watch a dependency drag your service into oblivion as long as it eventually returns 200.
**Avoid:** Relying on the request timeout alone to convert slowness into a "failure." By the time the timeout fires, the thread is already gone.

### 6. Order the pipeline: timeout (inner) → retry → circuit breaker (outer)
**Do:** In Polly v8 / resilience4j, wrap so the timeout fires per attempt, retries happen inside the breaker's view of "one call," and the breaker observes the final outcome.
**Why:** If retry is *inside* the breaker, one bad call is counted as N failures and the breaker trips too fast. If the breaker is inside retry, retries keep hammering an open breaker.
**Avoid:** Wiring three independent decorators in arbitrary order and assuming it works. This is the single most common Polly/resilience4j misconfiguration.

```csharp
// Polly v8 — order matters
new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRetry(new() { MaxRetryAttempts = 2, BackoffType = DelayBackoffType.Exponential })
    .AddCircuitBreaker(new() { FailureRatio = 0.5, MinimumThroughput = 20, BreakDuration = TimeSpan.FromSeconds(15) })
    .AddTimeout(TimeSpan.FromSeconds(2))   // innermost: per attempt
    .Build();
```

### 7. Design the fallback before you ship the breaker
**Do:** Decide, per breaker, exactly what happens when it's open: stale cache, a default object, a degraded UI, a 503 with a Retry-After. Implement and test that path explicitly.
**Why:** A breaker without a fallback just converts a slow failure into a fast one. Sometimes that's correct (faster 503 is better than thread starvation), but the choice should be deliberate.
**Avoid:** A "fallback" that calls a second flaky service synchronously. You've doubled the failure surface.

### 8. Emit state transitions as first-class events, alert on duration-Open
**Do:** Wire breaker events (`OnOpened`, `OnHalfOpened`, `OnClosed`) into OpenTelemetry / Micrometer. Track `state{closed|open|half_open}`, trip count, half-open success rate, and short-circuit count. Alert when a breaker stays Open longer than N minutes.
**Why:** Open breakers hide upstream errors — your usual "5xx rate from dependency X" alert goes silent because you stopped calling X. The breaker becomes the only honest signal.
**Avoid:** Logging only. State transitions buried in app logs are unfindable during an incident.

### 9. Don't share one breaker across unrelated calls
**Do:** Separate breakers for `/charge` and `/refund` if they hit different upstream subsystems, even on the same vendor. Likewise for read vs. write paths on the same database.
**Why:** A read-only replica being down should not stop you from writing. One breaker conflates two health signals and trips on the union.
**Avoid:** A single "stripe" breaker covering every Stripe endpoint your app uses.

### 10. Make 4xx not count (mostly)
**Do:** Configure the outcome classifier to count network errors, 5xx, gRPC `UNAVAILABLE`/`DEADLINE_EXCEEDED`, and slow calls. Exclude 4xx (except 429 in some setups, and never 408).
**Why:** A 404 or 401 means *your call* was wrong, not that the upstream is sick. Counting them trips the breaker during a bad deploy of your own service.
**Avoid:** The default "any exception trips" in toy implementations.

### 11. Accept per-pod state, or push to the mesh
**Do:** If you run N replicas, decide consciously whether N independent breaker states is acceptable (usually yes — they converge fast under shared traffic) or whether you need Envoy/Istio enforcement for a global view.
**Why:** Trying to synchronise breaker state across pods through Redis is a classic anti-pattern: you've added a new SPOF to protect against an SPOF.
**Avoid:** Building a "distributed circuit breaker" with shared storage. The pattern is intentionally local.

## Anti-patterns to recognize

- **Breaker around in-process code.** Wrapping a local function call "for safety." There's no thread pool to protect and no recovery semantics; you've added latency and a state machine for nothing. Just let the exception propagate.
- **Retries inside the breaker.** A 5x retry policy nested inside a 50% threshold breaker amplifies one failed call into five recorded failures and trips on a single bad request. Put the breaker outside the retry, or count attempt-bundles.
- **Shared breaker across unrelated calls.** One `httpClient` breaker covering payments, search, and analytics. Now a search outage stops payments. Split per dependency.
- **Breaker without timeouts.** The breaker only sees failures it can observe; a call that hangs forever never trips it. Always pair with a per-attempt timeout.
- **Health-check-driven breaker.** Driving breaker state from a separate `/health` poller. Health checks lie — only real call outcomes are truthful. Use traffic, not pings.
- **Breaker as load balancer.** Using state to "pick a healthier upstream." That's outlier detection's job at the mesh layer. A breaker decides *whether* to call, not *where*.
- **Set-and-forget thresholds.** Tuning once at launch and never revisiting. Traffic shape changes; what was a 50% sample at launch is now 5% of one second's traffic. Re-tune annually or when load changes by >2x.
- **Fallback hiding a real bug.** Returning cached prices from 2 weeks ago because the breaker has been Open for 2 weeks. The fallback worked; the alerting didn't. Alert on Open duration, not just upstream errors.

## Real-world usage patterns

**E-commerce checkout with a flaky payment provider.** Mid-size retailer, ~500 RPS at peak. A per-vendor breaker wraps each payment processor (Stripe, Adyen, a regional bank gateway). When one vendor's p99 jumps past 3s, the breaker opens and the checkout flow transparently fails over to a secondary processor. *Lesson:* the breaker is what makes the fallback path *cheap enough to use* — without fast failure, you can't try a second provider within the user's patience budget.

**LLM-powered feature with a primary and fallback model.** A SaaS app using GPT-class inference for summarisation. A breaker wraps the premium model; on Open, requests fall back to a smaller, faster model with a quality disclaimer. Slow-call detection is the dominant signal — 5xx is rare, but 45-second responses are common during provider incidents. *Lesson:* without slow-call detection, the breaker is decorative on LLM endpoints.

**Mesh-level outlier detection in a polyglot fleet.** A platform team running Istio across Go, Python, and Node services configures `DestinationRule.outlierDetection` once per namespace. Per-host ejection happens transparently; application code stays clean. *Lesson:* mesh enforcement is uniform but coarse — it sees HTTP status and latency, not business-level error codes. Keep an in-process breaker for the cases where "200 OK with `{error: ...}`" is a failure.

**Database connection breaker during failover.** A reporting service with a Postgres primary that occasionally fails over. A breaker around the connection pool fast-fails for 30 seconds after detecting connection refusals, letting the read-replica path take over instantly. *Lesson:* breakers belong around data tiers too — not just HTTP. The same starvation dynamics apply to connection pools.

## Operational checklist

- **Metrics:** are state, trip count, short-circuit count, half-open success rate, and time-in-Open all exported to your dashboard?
- **Alerting:** does an alert fire when *any* breaker stays Open for more than your SLO-determined threshold (typically 5–15 minutes)?
- **Fallback tested:** is the Open-state code path covered by an integration test that forces the breaker open?
- **Pipeline order:** is the order timeout → retry → breaker (innermost to outermost), and is it the same in every service?
- **Predicate sanity:** does your classifier exclude 4xx (other than 429/408)? Does it include slow calls?
- **Granularity audit:** is there exactly one breaker per logical dependency — not one per call site, not one shared across vendors?
- **Per-attempt timeout:** does every breaker have a sibling timeout that prevents hanging calls from going unobserved?
- **Onboarding:** can a new engineer find the breaker configuration in <2 minutes and explain what trips it?
- **Mesh vs library:** is it documented which layer owns breaking for each dependency, so the two don't fight?

## How this topic typically evolves in a codebase

Teams almost always start with no breaker at all. The first incident — a slow third-party API pinning every worker thread — produces a copy-pasted breaker around that one call, usually with `consecutive failures = 5` because that's what the README example showed. This works for a quarter.

The second phase is *proliferation*: every team adds its own breaker, each with different defaults, each emitting different metrics. A platform team eventually consolidates them behind a shared resilience library (typically `Microsoft.Extensions.Http.Resilience` in .NET shops, a `resilience4j-spring-boot3` starter in Java shops) so every outbound `HttpClient` has the same pipeline. This is the painful migration: someone has to delete every bespoke breaker and unify on the standard, often while debating threshold values team by team.

The third phase is *pushing concerns to the mesh*. As the org adopts Istio or Linkerd, outlier detection covers the per-host layer and application-level breakers shrink to where they add unique value: business-error classification and typed fallbacks. The endpoint state is *fewer, smarter* breakers in code plus uniform mesh policy. The teams that get to this stage stop talking about circuit breakers at all — they talk about SLOs and the breakers are just the mechanism.

## Further reading

- [Polly v8 circuit-breaker docs](https://www.pollydocs.org/strategies/circuit-breaker.html) — the canonical .NET reference; the "Defaults" and "Failure handling" sections are worth reading slowly.
- [resilience4j circuit-breaker docs](https://resilience4j.readme.io/docs/circuitbreaker) — best treatment of count-based vs time-based windows anywhere, and the spec for slow-call detection.
- [Envoy outlier detection](https://www.envoyproxy.io/docs/envoy/latest/intro/arch_overview/upstream/outlier) — explains why "circuit breaking" and "outlier detection" are two distinct features in Envoy, and why you usually want both.
- Michael Nygard, *Release It!* (2nd ed., 2018) — the chapter where the pattern was named. Still the most honest treatment of why stability patterns exist.
- [Christian Posta — Comparing Envoy and Istio Circuit Breaking with Hystrix](https://blog.christianposta.com/microservices/comparing-envoy-and-istio-circuit-breaking-with-netflix-hystrix/) — the clearest write-up of how mesh-layer breaking maps onto the Hystrix mental model people still carry.
- [Red Hat — Implementing and monitoring circuit breakers in OpenShift Service Mesh 3](https://developers.redhat.com/articles/2025/09/29/how-implement-and-monitor-circuit-breakers-openshift-service-mesh-3) — recent, concrete worked example of the mesh-side configuration and Prometheus alerts.
