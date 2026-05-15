# Idempotency

Idempotency is the property that running the same operation twice — or a hundred times — leaves the system in exactly the same state as running it once. The second call is a safe no-op, not a second side effect.

It matters because networks lie, clients retry, and message queues redeliver. If a payment, a webhook, or an email send is not idempotent, a single flaky connection can turn one charge into two and one notification into ten. Engineers reach for idempotency whenever an operation might be retried across a network boundary: payment APIs, at-least-once message consumers, webhook receivers, database migrations, and infrastructure scripts that are expected to be run repeatedly without harm. It is also the foundation on which "exactly-once" processing is built in practice — at-least-once delivery plus an idempotent consumer.

The light switch is the right analogy. Flicking the switch up once turns the light on; flicking it up four more times leaves the light on. The action describes a destination, not a step. A turnstile counter is the opposite — every push adds one, and there is no way to retry safely. `PUT /users/123 { "name": "Alice" }` is the switch. `POST /payments { "amount": 50 }` is the turnstile, which is why payment APIs require an idempotency key to make retries safe.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/general-concept/idempotency/
