# Circuit Breaker — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
A circuit breaker is a stateful proxy around a fallible operation (almost always a remote call) that uses a finite-state machine — **Closed**, **Open**, **Half-Open** — driven by observed call outcomes. While Closed, it forwards calls and records outcomes against a windowed health metric. When that metric crosses a threshold it transitions to Open, short-circuiting subsequent calls by throwing a sentinel exception (`BrokenCircuitException` in Polly, `CallNotPermittedException` in resilience4j) without touching the network. After a cool-down it transitions to Half-Open, admits a bounded number of trial calls, and either returns to Closed or back to Open based on their outcomes.

It is an instance of the **Stability Patterns** family popularised by Michael Nygard in *Release It!* (2007). It is best thought of as the "fail fast" complement to retries and timeouts.

### The core building blocks
- **State machine.** Three named states with deterministic transitions. Some implementations add a fourth virtual state, *Forced Open* / *Disabled*, for manual operator override.
- **Outcome classifier.** A predicate that decides whether a given result is a failure. Exceptions, HTTP 5xx, gRPC `UNAVAILABLE`, and crucially **slow calls** (latency over a threshold) are typical inputs.
- **Health window.** A data structure that aggregates recent outcomes. Three flavours dominate:
  - *Consecutive-count* — trip after N failures in a row (Hystrix-era, simple, brittle under bursty traffic).
  - *Count-based sliding window* — last N calls, compute failure ratio (resilience4j default, N = 100).
  - *Time-based sliding window* — outcomes in the last T seconds (resilience4j alternative; Polly v8's only model).
- **Threshold + minimum throughput.** A ratio plus a floor on sample size, so the breaker doesn't trip on 1 failure out of 1 call.
- **Break duration / reset timeout.** How long to stay Open before probing.
- **Half-open permit pool.** A bounded counter controlling how many trial calls may run concurrently.
- **Fallback.** Optional alternate path (cached value, default, degraded response) returned on short-circuit.

### How it relates to the broader landscape
The breaker sits in the **resilience / stability patterns** family alongside **retry**, **timeout**, **bulkhead**, **rate limiter**, **hedging**, and **fallback**. Retry assumes the failure is transient and tries *again now*; the breaker assumes it is sustained and refuses to try *at all*. Bulkhead bounds the *resources* a dependency may consume; the breaker bounds the *attempts*. In modern stacks (Polly v8, resilience4j) these are composable strategies in a single pipeline, not competing options.

## Where

### Where it runs / lives in the stack
A breaker is placed at the **client side of every outbound synchronous call**, one instance per logical dependency (or, more aggressively, per upstream host). It can live at four distinct layers:

1. **In-process library.** Polly (.NET), resilience4j (JVM), Opossum (Node), gobreaker (Go), pybreaker (Python). State is per-instance and per-process.
2. **Sidecar / data-plane proxy.** Envoy enforces connection-pool and outlier-detection circuit breaking transparently to the application. Linkerd's proxy similarly.
3. **Service mesh control surface.** Istio's `DestinationRule.trafficPolicy.outlierDetection` and `connectionPool` configures Envoy across the mesh declaratively.
4. **API gateway / edge.** Kong, NGINX, AWS API Gateway, and Azure APIM expose breaker-like upstream-health features.

The in-process and mesh variants are not equivalent. An in-process breaker can use rich, application-typed signals (specific exception types, business-level error codes, payload size) and can serve typed fallbacks. A mesh-level breaker only sees L4/L7 wire signals (HTTP status, TCP errors, latency) but applies uniformly across languages with zero code change.

### Where you typically encounter it
- **.NET / ASP.NET Core** with **Polly v8** wired into `HttpClientFactory` via `AddStandardResilienceHandler` (which composes timeout + retry + circuit breaker + rate limiter).
- **Spring Boot** with **resilience4j-spring-boot3** via the `@CircuitBreaker` annotation or Spring Cloud Circuit Breaker abstraction.
- **Istio / Envoy** meshes — outlier detection at the sidecar.
- **AWS SDKs** — internal client-side breakers and adaptive retry modes.
- **Node services** — Opossum wrapping `fetch`/Axios calls.
- **Database drivers** — many pool clients (e.g., HikariCP via resilience4j, npgsql via Polly) have breaker-like fast-fail behaviour for unreachable hosts.

### Ecosystem and tooling
- **For .NET:** Polly v8 (`Microsoft.Extensions.Http.Resilience` ships a curated Polly pipeline as the standard).
- **For JVM:** resilience4j (the recommended successor to Hystrix), Spring Cloud Circuit Breaker (vendor-neutral facade), Sentinel (Alibaba, popular in China).
- **For Node / Go / Python:** Opossum, sony/gobreaker and slok/goresilience, pybreaker and `tenacity` + custom wrapping.
- **For the mesh / edge:** Envoy, Istio, Linkerd, Consul Connect, Cilium service mesh.
- **For observability:** Polly emits OpenTelemetry metrics and events from v8; resilience4j exposes Micrometer metrics and an event bus; Envoy publishes `outlier_detection.*` and `circuit_breakers.*` stats to Prometheus.
- **Legacy / archival:** **Netflix Hystrix** has been in maintenance mode since 2018 and is not recommended for new work — Netflix itself directs new projects to resilience4j.

## When

### When the topic emerged and why
The pattern was named and codified by Michael Nygard in *Release It!* (Pragmatic Bookshelf, 2007), drawing on hard lessons from large e-commerce outages where a slow downstream pinned thousands of front-end threads. Netflix open-sourced **Hystrix** in 2012; it was the de facto JVM implementation for half a decade. Its architecture coupled the breaker with a *thread-pool bulkhead* per dependency — every call ran on a dedicated pool, isolating the caller from upstream slowness at the cost of context-switching overhead. Hystrix entered maintenance mode in 2018; **resilience4j** (Java 8, functional, lightweight, Micrometer-native) is the modern replacement. .NET followed a parallel path with Polly, whose v8 release (Nov 2023) rewrote the API around a unified `ResiliencePipeline` and added first-class telemetry.

### When to use it in a project
Reach for it when:
- You make **synchronous outbound calls** whose failure can stall caller threads or connections.
- A failing dependency has a **history of slow degradation**, not clean fast errors (slow-call detection is where breakers genuinely save you).
- You have an acceptable **fallback** (cached value, default, partial response, user-visible degradation) — or fast failure is itself better than long hangs.
- You operate at fan-in scale where one slow dependency can deplete a shared worker pool.

### When NOT to use it
Avoid it when:
- The call is **in-process**. Nothing to break, no failure to isolate.
- The work is **asynchronous and queue-based**. Use consumer-side backoff and dead-letter queues — the queue itself is the bulkhead.
- The operation is an **idempotent cheap read** and retry-with-jitter alone is sufficient.
- The request has **no acceptable fallback** and must eventually succeed; queueing or compensation may fit better than fast failure.
- The system is **small enough that a slow dependency cannot cascade** — the operational complexity (tuning thresholds, monitoring states, alerting) is not free.

## How

### How it works under the hood
Walk through a single request's lifecycle through a Polly-style pipeline (`Retry → CircuitBreaker → Timeout → HttpClient`):

1. **Pre-check.** The breaker reads its current state (atomic, lock-free in resilience4j and Polly).
2. **Open path.** If Open and the break duration has not elapsed, throw the short-circuit exception immediately. No thread is held, no socket allocated.
3. **Half-Open path.** If Open and the timer has elapsed, the breaker atomically transitions to Half-Open and resets a permit counter. The first N concurrent callers each decrement a permit and are admitted as **probes**. Excess concurrent callers in Half-Open are rejected as if Open.
4. **Closed path.** Forward the call. Start a stopwatch.
5. **Outcome capture.** On return, classify the result. Success, exception, slow call (latency over `slowCallDurationThreshold`).
6. **Window update.** Append the outcome to the sliding window. In resilience4j, the count-based window is a fixed-size ring buffer; the time-based window is a bucketed array (one bucket per second) with incremental aggregate updates so the failure-ratio computation is O(1) per call.
7. **Threshold evaluation.** Only after `minimumNumberOfCalls` have been recorded does the breaker compute the failure ratio. If it crosses `failureRateThreshold` *or* the slow-call ratio crosses `slowCallRateThreshold`, transition to Open and stamp the open-at timestamp.
8. **Half-Open resolution.** Once all permitted probes complete, compute their failure ratio. Below threshold → Closed and reset the window. Above → Open and restart the break duration (often with exponential backoff in production setups).

A short Polly v8 sketch, illustrative only:

```csharp
new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddCircuitBreaker(new()
    {
        FailureRatio       = 0.5,                  // trip at 50% failure
        MinimumThroughput  = 20,                   // need 20+ samples first
        SamplingDuration   = TimeSpan.FromSeconds(30),
        BreakDuration      = TimeSpan.FromSeconds(15),
        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .HandleResult(r => (int)r.StatusCode >= 500)
    })
    .Build();
```

Polly v8's documented defaults are `FailureRatio = 0.1`, `MinimumThroughput = 100`, `SamplingDuration = 30s`, `BreakDuration = 5s`. Resilience4j's defaults are `failureRateThreshold = 50`, `slidingWindowSize = 100`, `minimumNumberOfCalls = 100`, `waitDurationInOpenState = 60s`, `permittedNumberOfCallsInHalfOpenState = 10`, `slowCallDurationThreshold = 60s`. These are starting points, not production values — both libraries' authors are explicit that you must tune.

### Key trade-offs

| Choice | Gain | Give up |
|---|---|---|
| **Count-based window** | Simple, predictable memory | Distorted by traffic bursts and idle periods |
| **Time-based window** | Stable under variable traffic | More memory, age-based eviction overhead |
| **Per-host breaker** (Envoy outlier detection) | Ejects only the sick replica | Many tiny state machines, harder to reason about globally |
| **Per-service breaker** (typical library) | One mental model per dependency | A single bad replica can trip the whole route |
| **Short break duration (seconds)** | Fast recovery on transient blips | Higher risk of re-tripping on slow-recovering deps |
| **Long break duration (minutes)** | Lets upstream actually recover | User-visible outage extended |
| **Allow many half-open probes** | Faster confidence to close | Bigger herd against a still-fragile service |
| **Single half-open probe** | Gentlest probing | Slow signal, slow recovery |
| **Slow-call detection on** | Catches latency-only failures (the dangerous kind) | Requires picking a defensible latency threshold |

### Common failure modes
- **Window too small + low threshold.** Two timeouts trip a global breaker. Cause: `minimumThroughput` left at the default while real traffic is sparse.
- **Thundering herd on recovery.** When the breaker flips Open → Half-Open, every pending caller races to probe and re-overloads the upstream. Cause: no permit gating on probes, or wide permit pool. Mitigation: small probe pool, jittered break duration, or serialised single-probe recovery.
- **All-or-nothing breaker for a partial outage.** One bad replica out of ten trips the service-level breaker for everyone. Cause: per-service rather than per-host state. Mitigation: Envoy outlier detection at the sidecar.
- **Breaker hides a config bug.** 100% of calls fail because of a wrong URL; breaker sits Open forever, alarms silent. Cause: missing state-transition alerts. Fix: alert on Open-state duration, not just upstream errors.
- **Retries inside the breaker amplify failures.** A 5x retry policy inside a 50% threshold breaker makes the breaker count one failure as five. Cause: pipeline order. Fix: breaker outside retry, *or* tell the breaker to count attempt-bundles, not individual attempts.
- **Per-process state, multi-pod fleet.** Each pod has its own opinion. One pod still hammers the upstream. Cause: in-process breakers don't share state. Fix: accept the eventual-consistency, or push enforcement to the mesh.
- **Half-open starvation.** Under heavy load, every Half-Open transition burns probes instantly and almost always re-trips. Cause: upstream recovers slowly relative to your traffic. Fix: longer break duration, gradual ramp instead of binary closed/open.

## Why

### Why it exists
Synchronous RPC is the default in microservice architectures, and synchronous calls couple **liveness budgets**. If A waits on B and B waits on C, then C's latency becomes A's latency and C's failure becomes A's resource exhaustion. The single most expensive failure in production distributed systems is **thread-pool / connection-pool starvation** caused by waiting on a slow dependency — once the pool is empty, the caller stops serving *unrelated* traffic. The breaker exists to put a strict upper bound on how much of your own capacity a single sick dependency can consume.

### Why it looks the way it does
An obvious alternative design: a global health check daemon that pings each dependency and publishes "up/down" to all callers. This was tried and largely abandoned. The reasons:
- **Health checks lie.** A `/health` endpoint that responds 200 in 10ms says nothing about whether the *expensive* code path is healthy.
- **Real call outcomes are the only truthful signal.** A breaker is driven by the traffic that actually matters.
- **State must be local to be fast.** Consulting a central oracle on every call adds a network hop to every call — the exact thing you're trying to avoid.

The three-state machine specifically (rather than two: Open / Closed) exists to solve the **recovery problem**. With two states, you either keep the circuit Open forever (manual intervention) or you flip it Closed and immediately blast a recovering service with full traffic. Half-Open is the controlled re-introduction of load — a deliberately small probe whose only purpose is to extract a binary "are you better?" signal at minimal cost. The bounded permit count in Half-Open is what prevents the thundering herd from being baked into the pattern itself.

### Why it matters now
As of 2026, almost every service in a non-trivial backend calls at least one third-party API — payment, identity, LLM inference, search, analytics — whose tail latency you do not control. LLM and generative-AI endpoints in particular have wide and unpredictable latency distributions and frequent partial outages, and Polly v8's built-in `AddStandardResilienceHandler` was explicitly shaped around that workload. The pattern is not glamorous and not new, but it remains the single highest-leverage piece of code most backends never get around to writing. Service meshes have made it cheaper (one Istio `DestinationRule` covers the whole namespace), and observability libraries finally treat state transitions as first-class events. There is no successor pattern on the horizon — the next layer of innovation is *adaptive* breakers that tune thresholds from live traffic, but the underlying state machine is unchanged.

## Open questions / things to verify in practice
- Does the breaker correctly classify your specific failure mode? HTTP 429 should usually *not* trip the breaker; gRPC `DEADLINE_EXCEEDED` usually should. Verify the predicate.
- Under your real traffic shape, does `minimumThroughput` get reached often enough to be meaningful, or is the breaker effectively dormant outside peak hours?
- When the breaker is Open, does your fallback actually work end-to-end (cache populated, default value sensible, downstream UX acceptable), or have you just moved the failure?
- How long does it take an alert to fire after a breaker goes Open, and is that latency acceptable given your SLO?
- If you run N pods, do you accept N independent breaker states, or do you need mesh-level enforcement?
- Does your pipeline order (timeout → retry → breaker vs. breaker → retry → timeout) match what your library assumes? This is the single most common misconfiguration.
