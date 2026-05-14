# Circuit Breaker — Overview

> A wrapper around a remote call that stops trying when the other side is clearly broken, so one sick service doesn't drag your whole system down with it.

## The 30-second version
In a distributed system, services call other services. When one of those callees gets slow or starts failing, the naive thing is to keep calling it — and that's exactly how outages spread. A circuit breaker sits in front of the outbound call, watches the failure rate, and once things look bad it stops calling for a while. Callers get an instant error (or a fallback) instead of waiting on a doomed request. After a cool-down, the breaker cautiously tests whether the downstream is healthy again before resuming normal traffic.

## The mental model
Think of the breaker in your house's electrical panel. Under normal load, current flows through it and you never think about it. If something downstream shorts out, the breaker trips — it physically opens the circuit so the fault can't burn down the wiring upstream. You wait, fix the appliance, and flip it back on.

A software circuit breaker does the same job for a network call. Imagine your `OrderService` calls `PaymentService`. Payments is having a bad day — its database is locked, responses are taking 30 seconds before timing out. Every request from Orders eats a thread, a connection, some memory, for 30 seconds. Now Orders runs out of threads. Now the service calling Orders runs out of threads. Within minutes, a payments problem has become a checkout outage, an inventory outage, a homepage outage. This is **cascading failure**, and it's the single biggest reason small incidents become company-wide ones.

The breaker prevents that. After it sees, say, 50% of recent calls fail, it **opens**: every subsequent call returns immediately with a "circuit open" error. No thread held, no 30-second wait. The failing service gets breathing room to recover instead of being hammered by retries. After a timeout, the breaker enters a **half-open** state and lets a small number of trial requests through. If they succeed, it **closes** and traffic resumes. If they fail, it snaps open again.

The three states in one line each:
- **Closed** — normal operation, calls pass through, failures are counted.
- **Open** — calls fail instantly without touching the network.
- **Half-Open** — a probe lets a few real calls through to test recovery.

## What it is NOT
- **Not a retry.** Retries assume the failure is transient and try again *now*. A breaker assumes the failure is sustained and refuses to try at all. The two compose: retry inside, breaker outside.
- **Not a rate limiter.** Rate limiters cap traffic you *choose* to send. Breakers react to traffic that's already broken.
- **Not a timeout.** A timeout bounds a single call. A breaker uses timeouts as one signal among many to decide whether to keep calling.
- **Not a load balancer.** Breakers can live next to one (one breaker per upstream instance) but they don't pick where traffic goes — they decide whether traffic goes at all.

## When you would reach for it
- A synchronous call to another service whose failure can stall your own threads.
- A call to a flaky third-party API (payment gateway, SMS provider, geocoder).
- Any dependency where "fail fast with a fallback" is better UX than "spin for 30 seconds and then fail."
- High-fan-in services where one slow dependency can exhaust the whole worker pool.

## When you would NOT reach for it
- Pure in-process calls. There's nothing to break.
- Asynchronous, queue-based work where the consumer can retry on its own schedule — backoff and dead-letter queues fit better.
- Calls where every request must eventually succeed and there is no acceptable fallback; you may need different patterns (bulkheads, queueing, careful retries).
- Tiny systems with one downstream and no real risk of cascading failure. The complexity isn't free.

## Key vocabulary (just enough to keep reading)
- **Cascading failure** — one service's outage propagating upstream through synchronous calls.
- **Closed / Open / Half-Open** — the three breaker states.
- **Failure threshold** — the error rate or count that trips the breaker.
- **Cool-down (reset timeout)** — how long the breaker stays open before probing.
- **Fallback** — the alternative response returned when the breaker is open (cached value, default, friendly error).
- **Fail fast** — return an error immediately instead of waiting for a doomed call.
- **Bulkhead** — sibling pattern that isolates resource pools so one bad dependency can't drain all your threads.
- **Probe / trial request** — the half-open call used to test recovery.

## What's next
The next document answers What / Where / When / How / Why in detail — including how the breaker actually counts failures (sliding windows vs. consecutive counts), how to choose thresholds and cool-downs, where the breaker lives (in-process library vs. service mesh sidecar), and how it pairs with retries, timeouts, and bulkheads.
