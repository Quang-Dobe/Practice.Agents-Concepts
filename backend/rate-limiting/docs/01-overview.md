# Rate Limiting — Overview

> Rate limiting is the doorman of your API: it counts how often each caller knocks and turns them away once they knock too fast.

## The 30-second version
Rate limiting caps how many requests a client can make to your service in a given window of time. It exists because servers, databases, and downstream APIs all have finite capacity, and a single misbehaving client — a runaway script, a scraper, or an attacker — can starve everyone else. Engineers care because rate limiting is the cheapest, most reliable line of defense between "the service is healthy" and "the on-call phone is ringing at 3 a.m." It also doubles as a billing and fairness mechanism: free tier gets 100 requests/minute, paid tier gets 10,000.

## The mental model
Picture a popular nightclub with one bouncer at the door. The club holds 200 people comfortably, but tonight 5,000 want in. The bouncer doesn't try to stuff everyone inside — he hands out wristbands at a steady pace and tells the rest to wait or come back later.

Now picture each guest carrying a bucket. Every time they enter the club, a token gets dropped into their bucket. The bouncer refills each bucket slowly, say one token per second, up to a max of ten. If a guest tries to enter with an empty bucket, they're turned away with a polite "try again soon." This is literally the **token bucket** algorithm, and it's what Stripe, AWS, and most modern APIs use under the hood.

The key insight: rate limiting is not about blocking traffic, it is about **shaping** it. You let bursts through (the bucket starts full), but sustained abuse drains the bucket faster than it refills, and the offender is automatically held back.

## What it is NOT
- Not a **firewall**. Firewalls block by IP or port; rate limiters count behavior over time.
- Not a **load balancer**. Load balancers spread traffic across servers; rate limiters reject excess traffic outright.
- Not a **circuit breaker**. Circuit breakers trip when downstream services fail; rate limiters trip when upstream callers misbehave.
- Not a **quota**. Quotas are usually daily/monthly billing caps; rate limits are short-window protection (seconds to minutes).

## When you would reach for it
- Public APIs where any anonymous caller could hammer you.
- Login and password-reset endpoints, to slow down credential-stuffing attacks.
- Expensive endpoints (search, report generation, AI inference) that cost real money per call.
- Tiered SaaS pricing where free vs. paid users get different request budgets.
- Protecting a fragile downstream — a legacy system, a third-party API with its own limits, or a slow database.

## When you would NOT reach for it
- Internal service-to-service calls inside a trusted mesh — use timeouts, retries, and bulkheads instead.
- Hard fairness across very small numbers of users — explicit queues are clearer.
- Stopping a determined DDoS — that's a job for a CDN or scrubbing service like Cloudflare, not your app-layer limiter.

## Key vocabulary (just enough to keep reading)
- **Token bucket** — refill tokens at a steady rate; each request spends one. Allows bursts.
- **Leaky bucket** — requests queue and drain at a fixed rate. Smooths bursts into a flat line.
- **Fixed window** — count requests inside a clock-aligned window (e.g., 12:00:00–12:00:59). Simple, but allows double-bursts at window edges.
- **Sliding window** — a rolling time frame that fixes the edge-burst problem.
- **429 Too Many Requests** — the HTTP status code returned when a client is rate-limited.
- **Retry-After** — response header telling the client when it's safe to try again.
- **Backoff** — client-side strategy of waiting (often exponentially) before retrying.
- **Distributed rate limiter** — shared state (usually Redis) so limits work across many app servers.

## What's next
The next document answers What / Where / When / How / Why in detail — including how to pick an algorithm, where in your stack to put the limiter (edge vs. gateway vs. app), how to make it work across a fleet of servers, and how to communicate limits back to clients without breaking them.
