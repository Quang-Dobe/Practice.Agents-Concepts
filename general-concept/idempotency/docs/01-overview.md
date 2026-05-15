# Idempotency — Overview

> Idempotency is the property that doing something twice (or a hundred times) leaves the system in the same state as doing it once.

## The 30-second version

Networks lie. Clients retry. Queues redeliver. If your "charge the customer $50" operation runs three times because of a flaky connection, your customer is now $100 poorer than they should be. Idempotency is the discipline of designing operations so that repeated execution is **safe** — the second call is a no-op, not a second side effect. It is one of the few ideas that touches HTTP API design, message queues, payment systems, and database migrations all at once, which is why every senior engineer eventually internalizes it.

## The mental model

Think of a light switch with two states: **up** and **down**. If you flick it "up" once, the light is on. If you flick it "up" four more times, the light is still on. The *action* "set switch to up" is idempotent — the end state is what matters, not how many times you asked for it.

Now contrast that with a **counter on a turnstile**. Every push adds one. Push it five times and you have five. That operation is *not* idempotent — each call leaves a new mark.

Idempotent operations describe a **destination**. Non-idempotent operations describe a **step**.

A real example: `PUT /users/123 { "name": "Alice" }` says "make user 123's name be Alice." Run it once or a thousand times — same end state. But `POST /payments { "amount": 50 }` says "create a new payment of $50." Run it twice and you have two payments. That is why payment APIs hand you an **idempotency key**: a unique token the client generates and attaches to the request, so the server can recognize "I have seen this exact request before, here is the previous result" instead of charging again.

## What it is NOT

- **Not the same as "stateless."** Stateless means the server keeps no session between requests. An idempotent operation can absolutely change server state — it just converges to the same state on retry.
- **Not the same as "pure" (no side effects).** A pure function returns the same output for the same input *and* has no side effects. Idempotent operations are allowed to have side effects; they just must not *accumulate* them on repeat.
- **Not the same as "safe."** A "safe" HTTP method (like GET) does not modify state at all. Idempotent methods can modify state — they just do so in a way that is repeat-tolerant.
- **Not automatic.** Calling something `PUT` does not make it idempotent. You have to design the handler that way.

## When you would reach for it

- Any API endpoint a client might retry after a timeout or 5xx.
- Payment, billing, or "send email" operations where double-execution is user-visible damage.
- Message-queue consumers, where at-least-once delivery means duplicates are guaranteed eventually.
- Database migrations and infrastructure scripts you want to run repeatedly without breaking.
- Webhook receivers, since the sender will retry until it gets a 2xx.

## When you would NOT reach for it

- Pure read endpoints — `GET` is already idempotent by definition, no extra machinery needed.
- One-off scripts run by a human who watches them complete.
- Append-only event logs where every call is *meant* to be a new entry (recording analytics events, for instance).
- Internal trusted callers where retries are impossible and the extra complexity buys nothing.

## Key vocabulary (just enough to keep reading)

- **Idempotent operation** — repeating it yields the same end state as running it once.
- **Idempotency key** — a unique client-supplied token used to deduplicate retried requests.
- **At-least-once delivery** — messaging guarantee where duplicates are expected; idempotent consumers are the fix.
- **Exactly-once semantics** — the illusion of one-time processing, usually built on at-least-once delivery plus idempotency.
- **Safe method** — an HTTP method that does not modify state at all (GET, HEAD, OPTIONS).
- **Side effect** — any change to state outside the function: DB row, email, charge, log entry.
- **Replay** — re-running the same request, intentionally or otherwise.
- **Deduplication window** — the time span over which the server remembers a key has been seen.

## What's next

The next document answers What / Where / When / How / Why in detail — HTTP method semantics, idempotency-key storage strategies, the relationship to distributed-system delivery guarantees, and how exactly-once is built on top of this idea.
