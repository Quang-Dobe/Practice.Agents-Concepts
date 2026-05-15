# Idempotency — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, idempotency is the thing sitting between your public API and the side-effect layer — the charge, the email send, the row insert. The reverse proxy retries on 502, the mobile client retries on timeout, the partner integration retries on a stale connection, and every one of those retries lands on your handler with the original `Idempotency-Key`. The dedup store is what stops the second, third, and fourth attempts from each producing a real-world consequence.

In an event-driven system — Kafka consumer, SQS worker, EventBridge target, Lambda triggered by S3 — idempotency is non-optional. At-least-once delivery is the realistic guarantee on every major broker (yes, including "exactly-once" claims, which are exactly-once *within* the broker's transactional scope, not end-to-end). Your consumer **will** see duplicates; the only question is whether you noticed.

In infrastructure code (Terraform, Pulumi, Kubernetes operators, Ansible), idempotency is enforced at the controller level: every `Reconcile()` runs on every event, and a non-idempotent reconciler produces drift, ghost resources, or thrash that pages the on-call.

If you have ever debugged "why did this customer get charged twice", "why are there three Stripe customers for the same email", or "why did the welcome email arrive five times", you have already met this topic in production. You probably lost.

## Best practices

### 1. Make the client generate the key, and freeze it at first attempt
**Do:** Generate a UUIDv4 or ULID on the client *before* the first request and reuse it byte-for-byte across all retries of that logical operation. Persist it locally if the operation may survive a process restart.
**Why:** Server-generated keys are useless — the server cannot recognize a retry it never saw. Regenerating per attempt defeats the entire mechanism.
**Avoid:** Letting the SDK auto-generate a fresh key on each retry, or deriving the key from request fields that legitimately change (timestamps, nonces).

### 2. Acquire the key atomically — never check-then-act
**Do:** Use `INSERT ... ON CONFLICT DO NOTHING` (Postgres), `SET NX EX <ttl>` (Redis), or `PutItem` with `attribute_not_exists` (DynamoDB) in a single round-trip.
**Why:** Two concurrent retries during a network blip both see "key absent" with separate `SELECT` then `INSERT`, both pass, both execute. This is the single most common cause of "we have an idempotency table and we still double-charged."
**Avoid:** A `SELECT` followed by an `INSERT` in the same handler, even inside a transaction at READ COMMITTED — the gap is the bug.

### 3. Store a request fingerprint, not just the key
**Do:** Hash the canonicalized body (and ideally method + path) with SHA-256 and store it alongside the key. On replay, compare fingerprints and reject mismatches with `422` (per the IETF draft) or `400` (per Stripe).
**Why:** Without this, a client bug that reuses an idempotency key with a different payload silently returns the **wrong** cached response. The bug is invisible until a customer notices they got someone else's refund.
**Avoid:** Using the body hash *as* the key — that breaks legitimate retries that include a fresh timestamp or trace ID, and conflates "same intent" with "same bytes."

### 4. Persist the full response, including failures
**Do:** Cache status code, headers, and body on success **and** on 4xx. Replay byte-identically.
**Why:** Clients distinguish "permanently rejected, stop retrying" from "transient, retry" by status code. If the second attempt of a validation failure returns 201 because the handler ran again, the client cannot reason about anything.
**Avoid:** Caching only on `2xx`. The retry loop on a `422` will hammer the handler repeatedly and eventually succeed against a transient condition that should have been rejected.

### 5. Pick a TTL longer than your slowest legitimate retry source
**Do:** Default to 24 hours (Stripe's choice, well-tested). Stretch to 7 days if you have cron-driven replays or human-operated retry tools. Index the TTL column and sweep with a background job.
**Why:** Too short and a delayed retry re-executes; too long and the dedup table grows unbounded, hurting insert latency on a hot index.
**Avoid:** 5-minute TTLs (catches the websocket-blip retry, misses everything else) and "forever" (Postgres tables that nobody pruned for two years and now block every deploy).

### 6. Make the dedup write transactional with the business write
**Do:** When the side effect is a database write, insert the idempotency row and the business row in the **same transaction**. When the side effect is external (email, payment, webhook), use the **transactional outbox**: write idempotency row + outbox row atomically, then a separate publisher delivers the outbox row with its own idempotency to the downstream system.
**Why:** A handler that commits the business row, then crashes before writing the idempotency row, will double-execute on retry. The outbox is the only sane way to keep "we did the thing" and "we sent the message about the thing" in sync.
**Avoid:** "Best-effort" dedup writes after the side effect. They lose the race that matters.

### 7. Model the key as a state machine, not a boolean
**Do:** Track `in_flight`, `succeeded`, `failed` explicitly with timestamps. Return `409 Conflict` while `in_flight`, replay cached response on terminal states, sweep `in_flight` rows older than a safety window (e.g. 2× handler timeout) into a recoverable state.
**Why:** A handler crash mid-execution leaves an `in_flight` row forever; without a sweeper, that key is permanently bricked and the client gets `409` until the heat death of the universe.
**Avoid:** A single `exists` boolean — you cannot tell "still running" from "finished" from "crashed."

### 8. Prefer naturally idempotent operations when you can choose
**Do:** Model APIs as `PUT /resources/{client-supplied-id}` ("ensure this resource exists with this state") instead of `POST /resources`. Use `INSERT ... ON CONFLICT DO NOTHING` for "create or no-op." Use `kubectl apply`-style reconciliation for infrastructure.
**Why:** No dedup store, no fingerprinting, no TTL — the data model itself absorbs the duplicate. Less code, fewer failure modes.
**Avoid:** Bolting an idempotency-key system onto an endpoint that should have been a `PUT` in the first place. The complexity compounds.

### 9. Use conditional writes for entity-level idempotency
**Do:** For updates, use `If-Match: "<etag>"` (HTTP) or `WHERE version = ?` (SQL) to make the *update* idempotent against concurrent modification.
**Why:** Two retries of the same `If-Match` request either both match (succeed identically) or both miss (fail with `412 Precondition Failed`). The response is meaningful and repeatable without a dedup table.
**Avoid:** Last-write-wins on retries — the second attempt clobbering an unrelated concurrent edit is silent data loss.

### 10. Dedup on the consumer, not just the producer
**Do:** In a Kafka/SQS consumer, derive a dedup key from the message (event ID, business primary key, offset+partition) and check it against an idempotency store before applying side effects. Commit the offset only after the side effect is recorded.
**Why:** Kafka's idempotent producer (`enable.idempotence=true`) prevents duplicates from a single producer session into a single partition. It does **not** prevent consumer-side duplicates from rebalances, replays, or offset reset. SQS FIFO dedup windows are 5 minutes — nothing for a slow consumer.
**Avoid:** Trusting "exactly-once" claims at the broker layer and skipping consumer-side dedup. End-to-end exactly-once is at-least-once delivery plus an idempotent consumer; there is no shortcut.

## Anti-patterns to recognize

- **"Every POST is idempotent because we use UUIDs."** A UUID in the body is not an idempotency key unless the server actively dedups on it. Fix: explicit `Idempotency-Key` header with server-side enforcement.
- **Hashing the body as the dedup key.** Breaks retries that mutate auxiliary fields (timestamps, trace headers) and conflates two legitimate identical requests into one. Fix: client-supplied opaque key plus body fingerprint for *validation*, not identity.
- **Different status code on replay.** Returning `201` first, then `200` on replay is allowed by RFC 9110, but returning `201` then `409` because "this already exists" makes clients treat the replay as an error. Fix: replay the exact original response.
- **Idempotency at the edge, not the worker.** API dedups, enqueues a job, queue redelivers, worker executes twice. Fix: dedup at the layer that owns the side effect — usually the worker, sometimes both.
- **The Stripe-style key-reuse trap.** Same key, different params, server returns the original cached response. Customer wired $500 instead of $50 and never knew. Fix: store fingerprint, reject mismatches with `422`/`400`.
- **Bricked in-flight rows.** Handler crashes mid-execution, row stuck `in_flight`, every retry gets `409` forever. Fix: sweeper that resets rows older than `max_handler_runtime × 2`.
- **Side effects outside the dedup transaction.** Transaction retries, email sends twice. Fix: transactional outbox pattern, or move the side effect inside the transaction (rare; usually impossible for external calls).
- **TTL too short for the retry source.** Cron retries 26 hours later, sees a fresh key, double-processes. Fix: model the retry distribution and set TTL to the 99.9th percentile.

## Real-world usage patterns

### Payment processors (Stripe, Adyen, Square)
A merchant calls `POST /v1/charges` with `Idempotency-Key: <client-uuid>`. The server stores the key with the response under a 24-hour TTL; replays return the exact same response, including failure responses. Reusing the key with different params returns `400` (Stripe) — protecting the client from its own bugs.
**Non-obvious lesson:** the cached response is part of the contract. Stripe explicitly documents which fields are part of the dedup record. If you redesign your response shape without rotating keys, you can replay a stale schema to a client that no longer understands it.

### Kafka consumers in a financial ledger
A consumer reads `payment.requested` events, writes to a ledger, commits the offset. Duplicates arrive on consumer rebalance. The handler computes a dedup key as `event.id` and does `INSERT INTO ledger (event_id, ...) ON CONFLICT (event_id) DO NOTHING RETURNING *`. If no row returns, it skips downstream side effects.
**Non-obvious lesson:** the dedup *is* the business write. Conflating them into one statement is what makes the pattern reliable — separate "have I seen this?" check and ledger insert is the same check-then-act race in a different costume.

### Kubernetes controller reconciling external resources
A controller watches a `DatabaseInstance` CRD and provisions an RDS database. `Reconcile()` runs on every event — possibly hundreds of times during a deploy. The implementation always reads RDS for an instance with `tag:k8s-uid=<crd-uid>` before creating; the UID is the idempotency key.
**Non-obvious lesson:** the cloud provider's tag is your idempotency store. You do not need a separate database because the desired-state model — "exactly one RDS instance per CRD UID" — encodes the dedup. This is the cheapest possible form.

### Webhook receiver for a payments partner
The partner retries until it gets `2xx`. Receiver dedups on `(partner_id, webhook_event_id)` in Postgres with a 30-day TTL. Returns `200` immediately after persisting the event to an inbox table; the actual processing is async.
**Non-obvious lesson:** the inbox pattern decouples "did we receive it" from "did we process it." The webhook sender is satisfied by the `200`; if processing fails, your internal retry mechanism handles it without the partner re-firing.

## Operational checklist

- **Monitoring:** dashboard for `idempotency_hit_rate` (replays / total requests), `in_flight_age_seconds` (p99), `fingerprint_mismatch_count` (should be near zero — spikes mean a client bug), and dedup table row count growth.
- **Failure handling:** is there a sweeper for `in_flight` rows older than 2× handler timeout, and is it tested by killing a handler mid-execution in staging?
- **Failure handling:** when the dedup store is down, does the API fail closed (reject the request) or fail open (process without dedup)? Document the choice; both are defensible, the silent choice is not.
- **Security:** idempotency keys should be opaque and unguessable when they cross trust boundaries — a predictable key from one tenant could replay another tenant's response. Scope by tenant in the lookup.
- **Cost:** dedup table size × storage cost + index maintenance. A 1KB row × 10M req/day × 7-day TTL = 70 GB of hot data; plan the index strategy and TTL sweeper accordingly.
- **Onboarding:** can a new engineer point to the exact line that performs the atomic acquisition? If they cannot, the implementation is too clever.
- **Testing:** is there an integration test that fires two concurrent requests with the same key and asserts only one side effect occurred? Without it, the race is unverified.
- **Schema evolution:** is the cached response schema versioned, so replays after a deploy do not return a shape the client no longer parses?

## How this topic typically evolves in a codebase

Teams usually start with **zero idempotency** — a few `POST` handlers, no dedup, "we'll add it when it's a problem." It becomes a problem the first time a payment is double-charged, a welcome email goes out three times, or a partner's webhook receiver fires a duplicate downstream. The first fix is almost always **per-endpoint, in-memory dedup** (a Redis `SETNX` with a short TTL) wired into a single handler. This works until the second handler needs it, the dedup logic gets copy-pasted, and the in-memory store loses keys on restart.

The migration that hurts is **going from per-endpoint to a shared, durable idempotency layer**: extracting a middleware, picking a real store (Postgres for transactional co-location, Redis for hot path with a Postgres backstop), defining the state machine, building the sweeper. This is a multi-week project at any reasonable scale because it touches every write endpoint and the schemas of cached responses. Teams that defer it often end up with three different idempotency implementations across services that disagree on TTL, response shape, and conflict semantics.

End-state is usually a **shared library** (or sidecar/middleware) that enforces the contract uniformly, plus consumer-side dedup in every queue worker, plus naturally idempotent APIs (`PUT` with client-supplied IDs) for new endpoints. The architectural lesson: idempotency is cheaper to design in than to retrofit, and the retrofit cost grows superlinearly with the number of endpoints.

## Further reading

- [Stripe — Designing robust and predictable APIs with idempotency](https://stripe.com/blog/idempotency) — the canonical engineering blog post; explains the lifecycle, the response cache, and the conflict semantics with production scars showing.
- [Stripe API Reference — Idempotent requests](https://docs.stripe.com/api/idempotent_requests) — exact wire format and error behavior; useful as a spec when you implement your own.
- [IETF draft-ietf-httpapi-idempotency-key-header-07](https://datatracker.ietf.org/doc/html/draft-ietf-httpapi-idempotency-key-header-07) — current standardization effort; defines `422` for fingerprint mismatch and `409` for in-flight conflict.
- [RFC 9110 §9.2.2 — Idempotent Methods](https://www.rfc-editor.org/rfc/rfc9110.html#name-idempotent-methods) — the authoritative HTTP definition; clarifies that idempotency is about server-side effect, not response.
- [Microservices.io — Transactional Outbox pattern](https://microservices.io/patterns/data/transactional-outbox.html) — the standard fix for "side effect outside the dedup transaction."
- [Confluent — Exactly-Once Semantics in Kafka](https://www.confluent.io/blog/exactly-once-semantics-are-possible-heres-how-apache-kafka-does-it/) — what Kafka's transactional API actually guarantees, and what it does not (consumer-side dedup is still your job).
