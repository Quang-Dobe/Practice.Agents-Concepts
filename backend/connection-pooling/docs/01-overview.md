# Connection Pooling — Overview

> Connection pooling is keeping a small stash of already-open database connections around and lending them out, instead of opening a fresh one every time your app needs to talk to the database.

## The 30-second version

Opening a database connection is surprisingly expensive: a TCP handshake, a TLS handshake, authentication, session setup — often 20–100ms of pure setup before you send a single query. If a web app opens one per request, most of the request budget is spent just saying hello. Connection pooling fixes this by opening N connections up front, handing them out to callers, and putting them back on the shelf when done. The result is faster responses and a database server that stops sweating under load.

## The mental model

Think of a busy hotel with one concierge desk. Every guest needs to talk to the concierge, but the concierge is on the top floor and has to walk down each time — slow, and the elevator gets clogged.

The hotel's fix: hire ten concierges, station them permanently in the lobby, and have guests take a numbered ticket. A guest walks up, grabs whichever concierge is free, gets their answer, and hands the concierge back. If all ten are busy, the guest waits in a short line. When traffic dies down at 3am, most concierges just stand around ready.

The pool is the lobby. The concierges are open connections. Your application code is the guest with the ticket. Your ORM or driver is the ticket dispenser, hiding all of this from you.

The interesting knobs fall out of the analogy: How many concierges? How long does a guest wait before giving up? What if a concierge falls asleep — do we check they're still awake before handing them to the next guest?

## What it is NOT

- Not a **cache**. Caches store query *results*. Pools store the *pipes* you send queries through.
- Not a **load balancer**. Pools sit inside one app talking to one database (or one cluster endpoint). They don't decide which database to hit.
- Not **HTTP keep-alive**, though the idea is the cousin. Keep-alive reuses TCP for HTTP; connection pools reuse full authenticated DB sessions.
- Not free. A pool is state — misconfigured, it becomes the bottleneck it was meant to fix.

## When you would reach for it

- Any web service or API that talks to a relational database under real traffic.
- Long-running workers that fire many small queries — a fresh connection per query would dominate cost.
- Serverless functions fronted by a pooler like PgBouncer or RDS Proxy, because the functions themselves can't hold a pool.
- Any client library where "open connection" shows up hot in a flamegraph.

## When you would NOT reach for it

- A one-shot CLI script that runs one query and exits — open, query, close, done.
- Extremely low-traffic internal tools where a single connection covers everything.
- Situations where each caller genuinely needs isolated session state (temp tables, session variables) and cannot tolerate a reused connection — unless the pool explicitly resets sessions.

## Key vocabulary (just enough to keep reading)

- **Pool** — the collection of reusable connections.
- **Min / max pool size** — floor and ceiling on how many connections stay open.
- **Acquire (checkout)** — borrowing a connection from the pool.
- **Release (checkin)** — returning it when done.
- **Connection lifetime** — how long a connection may live before being retired.
- **Idle timeout** — how long an unused connection sits before the pool closes it.
- **Acquire timeout** — how long a caller waits for a free connection before failing.
- **Validation / liveness check** — a ping (e.g. `SELECT 1`) to confirm a connection is still alive before lending it out.
- **External pooler** — a separate process (PgBouncer, RDS Proxy) that pools on behalf of many apps.
- **Connection leak** — code that acquires but never releases; the classic pool-killer bug.

## What's next

The next document, `02-deep-dive.md`, answers What / Where / When / How / Why in detail — pool sizing math, the difference between session and transaction pooling, what actually happens on `acquire()`, and why your pool size should almost never match your thread count.
