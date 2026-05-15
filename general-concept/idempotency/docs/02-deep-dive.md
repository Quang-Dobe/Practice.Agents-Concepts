# Idempotency — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

A function `f` is **idempotent** when `f(f(x)) = f(x)` for every `x` in its domain. Generalized to operations on a system: applying the operation `n` times (for any `n ≥ 1`) leaves the system in the same observable state as applying it once. The operation may still produce side effects on the *first* application — what idempotency forbids is *accumulating* further state change on subsequent identical applications.

Three near-neighbors that get confused with it:

- **Pure / referentially transparent.** A pure function has no side effects and is trivially idempotent. Idempotent operations are a strict superset: they are allowed to write to a database, send a webhook, or charge a card, provided repeat invocations converge to the same end state.
- **Stateless.** A property of the *handler*, not the operation. A stateless server forgets between requests; an idempotent operation can still mutate persistent state.
- **Commutative.** `f(g(x)) = g(f(x))`. Idempotency is about repetition of the *same* operation; commutativity is about reordering *different* operations. They are independent — one does not imply the other.

### The core building blocks

Operations split into two ergonomic classes:

- **Naturally idempotent operations.** The semantics themselves carry the property: `PUT /users/123` ("make the user be this"), `DELETE /users/123` ("ensure absent"), `SET counter = 5`, `INSERT ... ON CONFLICT DO NOTHING`, `kubectl apply`. These describe a target state, not a delta.
- **Operations made idempotent via keys.** Inherently delta-shaped operations — `POST /payments`, `INSERT INTO orders`, `INCREMENT balance` — that need an external mechanism to deduplicate retries. The standard mechanism is a client-supplied **idempotency key** correlated against a server-side **idempotency store**.

### How it relates to the broader landscape

Idempotency belongs to the family of **reliability primitives** that compensate for the realities of asynchronous networks: timeouts, partitions, redelivery. Its siblings are **retries with backoff** (the *cause* of duplicate requests), **deduplication** (the *implementation* on the receive side), **conditional writes / optimistic concurrency** (a tighter form addressing in-flight concurrent updates), and **transactions** (an alternative for atomic multi-step mutations within a single trust boundary). Any production system that takes correctness seriously layers several of these together; idempotency is the one that travels across trust boundaries.

## Where

### Where it runs / lives in the stack

Idempotency is enforced at the **application layer** of whichever component owns the side effect. It cannot be solved at the transport layer — TCP guarantees no duplicates *within a single connection*, but the moment a client retries on a fresh connection (because of a timeout, a load-balancer reset, or a process restart), TCP has nothing to say. The same logic explains why messaging systems advertise at-least-once delivery: the protocol cannot tell "ack lost" from "message lost", so it must redeliver, and dedup is pushed up to the consumer.

### Where you typically encounter it

- **HTTP APIs governed by RFC 9110.** PUT, DELETE, GET, HEAD, OPTIONS, and TRACE are defined as idempotent; POST and PATCH are not. The spec is explicit that idempotency is about the *intended effect on the server*, not the response — a server may legitimately return different status codes for repeated requests (e.g. `200` then `204`).
- **Payment APIs.** Stripe, Adyen, Square, and Braintree all expose an `Idempotency-Key` request header for create-charge / create-payment-intent endpoints. Stripe stores the response under that key and replays it on retry for at least 24 hours.
- **Message brokers.** Kafka's `enable.idempotence=true` (default since 3.0) guarantees per-partition dedup on the producer side; consumer-side idempotency is still the application's job. SQS, RabbitMQ, NATS, and Pub/Sub all document at-least-once as the realistic guarantee.
- **Database upserts.** `INSERT ... ON CONFLICT`, `MERGE`, `REPLACE INTO`, and `SETNX` are the SQL/Redis primitives that translate "create or no-op" into a single atomic statement.
- **Kubernetes controllers and operators.** Every `Reconcile()` function must be idempotent — controllers are looped and re-run on every event; non-idempotent reconcilers produce drift, duplicate resources, or thrash.
- **Terraform / Pulumi / CloudFormation.** `plan` and `apply` are repeated against the same desired-state file. Resources are expected to be idempotent through their lifecycle (`create`, `update`, `delete`); providers that get this wrong cause "resource already exists" errors on retry.

### Ecosystem and tooling

- **For HTTP APIs:** the IETF draft `draft-ietf-httpapi-idempotency-key-header-07` (active as of 2026) standardizes the `Idempotency-Key` header. MDN documents it. Most frameworks ship middleware: ASP.NET Core has community packages, Express/Fastify have `express-idempotency`, and the AWS SDKs add idempotency tokens for `Create*` calls automatically.
- **For storage of idempotency records:** Redis (`SETNX` + TTL) for hot dedup, PostgreSQL (`INSERT ... ON CONFLICT` with a TTL column or partial index) for durable dedup, DynamoDB with a conditional `PutItem` and TTL attribute for serverless workloads.
- **For messaging:** Kafka's transactional API + idempotent producer; AWS SQS FIFO queues with `MessageDeduplicationId`; Inbox/Outbox pattern libraries (e.g. Debezium, Wolverine, MassTransit) for transactional dedup against a relational store.
- **For declarative systems:** Helm, Kustomize, Ansible (with `state: present` semantics), Chef (resource-action model), Puppet.

## When

### When the topic emerged and why

The term `idempotent` is mathematical (Benjamin Peirce, 1870), but its computing relevance crystallized in two waves. The first was **HTTP/1.0 (RFC 1945, 1996)** and HTTP/1.1 (RFC 2616, 1999), which named the property and assigned it to specific methods to give caches and proxies safe retry rules. RFC 9110 (June 2022) is the current consolidated authority. The second wave was the rise of **distributed messaging and microservices in the 2010s**: as systems were carved into network-separated pieces, every internal call inherited the same "did my request actually land?" ambiguity that browsers had been dealing with for a decade. Stripe's 2017 engineering blog post on idempotency keys is widely credited with popularizing the application-level pattern as a first-class API design concern.

### When to use it in a project

Reach for it when:

- A client may retry across a network boundary you do not control (browser, mobile app, partner integration).
- You consume from an at-least-once queue (Kafka, SQS, Pub/Sub, RabbitMQ — i.e. essentially all of them).
- The operation has user-visible or money-visible side effects: charge, refund, email, SMS, inventory mutation, account creation.
- You build a webhook **receiver** — senders retry until 2xx, so duplicates are guaranteed.
- You run a reconciler against a desired-state model (operators, IaC, GitOps).
- A human can double-click a submit button.

### When NOT to use it

Avoid it (or skip the extra machinery) when:

- The operation is already naturally idempotent and you would only be paying for ceremony — adding a key to `GET` or to a true `PUT` rarely earns its keep.
- The endpoint is append-only by design (analytics events, audit logs, immutable event streams) where every invocation is *supposed* to be a new entry.
- Calls are internal between two trusted services with synchronous, bounded retries and the cost of a duplicate is acceptably low — sometimes a single retry budget and a metric are cheaper than a dedup store.
- The dedup window you would need to cover (months, indefinitely) makes storage cost unreasonable.

## How

### How it works under the hood

For naturally idempotent operations the mechanism is **state convergence**: the handler reads the current state, computes the diff against the requested state, and applies it — second call sees the diff is empty. `kubectl apply`, `PUT /users/123`, and Terraform follow this shape.

For the keyed pattern (POST + `Idempotency-Key`), the lifecycle is a small state machine:

```
client generates key K (UUID v4, ULID, or similar)
        │
        ▼
  POST /resource, Idempotency-Key: K, body B
        │
        ▼
  server: INSERT INTO idempotency (key=K, fingerprint=hash(B),
                                   status='in_flight', ttl=24h)
          ON CONFLICT (key) DO NOTHING
        │
        ├── insert succeeded ──► execute handler
        │                          │
        │                          ├── success ─► UPDATE row SET status='succeeded',
        │                          │              response_code=..., response_body=...
        │                          │
        │                          └── failure  ─► UPDATE row SET status='failed',
        │                                          response_code=..., response_body=...
        │
        └── insert conflicted ──► row exists
                                    │
                                    ├── fingerprint mismatch ─► 422 Unprocessable
                                    │                            (same key, different body)
                                    ├── status='in_flight'    ─► 409 Conflict
                                    │                            (concurrent original still running)
                                    └── status terminal       ─► replay cached response
```

Key design points:

1. **Client generates the key.** Server-generated keys defeat the purpose — the client needs a stable identifier to send on retry.
2. **Atomic acquisition.** The `INSERT ... ON CONFLICT DO NOTHING` (or Redis `SET key value NX EX ttl`) is what prevents two concurrent retries from both executing the handler.
3. **Request fingerprint.** A SHA-256 over the canonicalized body (and sometimes method + path) detects "same key, different payload" — a client bug that would otherwise silently return the wrong cached result.
4. **Persist the response, not just the fact of execution.** Replay must return byte-identical status + body, including 4xx/5xx, so the client sees the same outcome on every retry.
5. **TTL.** 24 hours is the Stripe default and a reasonable starting point — long enough to outlast retry storms, short enough to cap storage. Beyond TTL, a retry with the same key is treated as a fresh request.

Conditional writes — `If-Match: "<etag>"` per RFC 9110 §13, or `WHERE version = ?` in SQL — give a related but distinct guarantee: they make an *update* idempotent against concurrent modification rather than against retried delivery. Two retries of the same `If-Match` request will either both succeed (matching the ETag) or both fail (the resource moved on); a `412 Precondition Failed` is a meaningful, repeatable answer.

### Key trade-offs

| Design choice | What you gain | What you give up |
|---|---|---|
| Client-supplied key (vs server-supplied) | Survives client crash + retry | Clients can fingerprint-bug their own requests |
| Persistent store (Postgres) vs in-memory (Redis) | Durability across restarts, transactional with business writes | Higher latency, more storage cost |
| Long TTL (days) vs short TTL (minutes) | Catches slow retries, scheduled-job replays | Larger dedup table, longer "stuck in-flight" risk |
| Synchronous lock during in-flight (409) vs queue + wait | Simple, no head-of-line blocking | Client must implement its own wait/retry on 409 |
| Fingerprint validation strict vs lax | Catches client bugs, prevents wrong replay | Canonical hashing across languages is fiddly |
| Per-resource scope vs global | Reuse short keys safely | More complex lookup, scope-leak bugs |

### Common failure modes

- **Non-atomic acquisition.** Two retries both see "key absent", both execute, both write — classic check-then-act race. Cause: separate `SELECT` and `INSERT` instead of an atomic upsert.
- **Storing the request before executing.** A crash between the insert and the handler call leaves a permanent "in_flight" row; later retries get `409` forever. Cause: missing recovery on stuck records (some systems unstick after a configurable timeout; others require manual cleanup).
- **Forgetting to persist 4xx/5xx.** Replays of a request that originally failed validation execute the handler again. Cause: confusing "idempotent" with "successful".
- **TTL shorter than the longest plausible retry.** Cron-driven retries after 24h see a fresh key and double-process. Cause: TTL chosen by storage cost without modeling retry behavior.
- **Mutable request bodies.** Client adds a timestamp on retry, fingerprint mismatches, server returns 422 and the operation is now permanently stuck. Cause: not freezing the payload at first-attempt time.
- **Idempotent at the API edge but not in the worker.** API dedups, then enqueues; queue redelivers; worker double-executes. Cause: treating "the API is idempotent" as sufficient when the actual side effect happens downstream.
- **Side effects outside the dedup transaction.** Handler writes to DB inside a transaction but emits an email or external API call separately; transaction retries, email sends twice. Cause: missing transactional outbox.

## Why

### Why it exists

Networks have three valid outcomes for any request, not two: **success**, **failure**, and **unknown**. A timeout is the third case — the client cannot distinguish "server received and processed, ack lost" from "server never received". The only safe response to "unknown" is "retry"; the only safe way to retry a side-effectful operation is to make it tolerant of duplicates. Idempotency is the discipline that turns an inherently three-valued outcome into a two-valued one (it succeeded, or it didn't, and asking again is free).

This connects directly to the **Two Generals Problem** and the FLP impossibility result: there is no asynchronous protocol that can guarantee both delivery and non-duplication. Given that constraint, the engineering choice is "lose messages (at-most-once)" or "duplicate messages (at-least-once)". For anything that matters, the industry picks at-least-once and pushes deduplication to the application — which is exactly what idempotency provides.

### Why it looks the way it does

The obvious alternative to client-supplied keys is **server-side fingerprinting** — hash the request body and dedup on that. It fails in two real cases: (1) a client legitimately wants to send the same payload twice (two identical refunds to the same customer for two real purchases) and the server cannot tell them apart, and (2) idle retries with mutated metadata (a new timestamp header) get different fingerprints and execute twice. The client-generated key sidesteps both: it externalizes the "is this a retry?" decision to the only party that actually knows.

The other obvious alternative is **distributed transactions / two-phase commit** between client and server. 2PC works inside a coordinated cluster but breaks down across organizational and network trust boundaries: it requires both ends to participate in a coordinator's protocol, holds locks across network round-trips, and degrades catastrophically under partition. Idempotency keys are a cheaper, asymmetric protocol — only the server holds state, the client just resends — that works fine across the public internet.

### Why it matters now

As of 2026, three forces keep this topic central:

- **Event-driven and serverless architectures.** Lambda retries, SQS redeliveries, EventBridge replays, and Step Functions all assume the handler is idempotent. The default invocation model *is* at-least-once.
- **LLM-agent orchestration.** Agents retry tool calls, fan out parallel attempts, and replay traces during evaluation. Tools without idempotency keys produce double-bookings and double-emails in agent demos with embarrassing regularity.
- **AI-assisted code generation.** Generated handlers tend to look correct on the happy path and fail open under retries. Idempotency is one of the few correctness properties a human reviewer must still hold in their head — automated review tools are not yet reliable at catching it.

The concept is not new and it is not changing; the surface area where it bites is expanding.

## Open questions / things to verify in practice

- For your storage backend, what is the *exact* atomic primitive — `INSERT ... ON CONFLICT DO NOTHING`, `SET NX`, `PutItem` with `attribute_not_exists`? Confirm it is truly atomic under your isolation level (read-committed is usually fine; SERIALIZABLE has surprises).
- What is your TTL, and is it longer than your slowest retry source (cron, dead-letter requeue, manual replay tool)?
- Do you cache the *full response* on the idempotency record, including error responses, or only success?
- What happens to a row stuck in `in_flight` because the handler crashed mid-execution? Is there a sweeper, and what is its safety window?
- How do you canonicalize the request body for fingerprinting across clients (key ordering, whitespace, numeric precision)?
- Is the dedup write transactionally co-located with the business write, or can they diverge? If they can diverge, do you have an outbox?
