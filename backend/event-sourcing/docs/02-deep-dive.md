# Event Sourcing — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Event sourcing is a persistence pattern in which the authoritative state of a system is a totally-ordered, immutable, append-only sequence of domain events, and any queryable representation of state is a deterministic fold over that sequence. Writes produce events; reads consume projections of events; the events themselves are never updated or deleted. The pattern was named by Greg Young around 2006 and grew out of Domain-Driven Design — it is the persistence half of what DDD calls the aggregate.

### The core building blocks

- **Event** — an immutable record of something that already happened, named in the past tense (`OrderPlaced`, `ItemRemoved`). Carries a payload, a stream id, a stream version, a global position, an event id (UUID), and a timestamp.
- **Stream** — the per-aggregate sequence of events, identified by an id like `order-7`. Each event in the stream has a monotonically increasing **stream version** (0, 1, 2, …).
- **Event store** — the database that persists streams. Exposes at minimum `appendToStream(streamId, expectedVersion, events)` and `readStream(streamId, fromVersion)`.
- **Aggregate** — the in-memory domain object. Loaded by replaying its stream; new commands produce new events that are appended to the same stream.
- **Command** — an *intent* to change state (`PlaceOrder`). Validated by an aggregate, which either accepts it and emits one or more events or rejects it.
- **Projection / read model** — a materialised view derived from one or more streams. Optimised for queries; rebuildable by replaying events from position 0.
- **Subscription** — a long-lived reader that pulls events from the store in order and feeds projections, integration handlers, or process managers.
- **Snapshot** — an optional cached aggregate state at version N, so replay can start from N instead of 0.
- **Upcaster** — code that transforms an old event payload into the current schema at read time.

### How it relates to the broader landscape

Event sourcing sits in the family of *log-oriented persistence* alongside write-ahead logs (Postgres WAL, RocksDB), distributed commit logs (Kafka, Pulsar), and CRDT operation logs. It is distinct from each: a WAL is an implementation detail of a row store; Kafka is a transport with retention but typically no per-aggregate concurrency control; CRDT logs target conflict-free merging across replicas rather than domain modelling. Event sourcing's closest cousin is **CQRS** — they are independently useful but compose naturally, because event sourcing already separates the write model (events) from the read model (projections).

## Where

### Where it runs / lives in the stack

Event sourcing lives at the **persistence layer of a service**, not on the wire. The event store replaces (or sits alongside) a relational database for that service's write side. Projections may live in the same store, in a separate query database (Postgres, Elasticsearch, Redis, ClickHouse), or both. Cross-service eventing is a separate concern: events leaving the service over a broker are typically *integration events*, often a translated subset of the internal *domain events*, to avoid leaking aggregate internals.

### Where you typically encounter it

- **Financial / fintech** — Stripe-like ledgers, exchange trading engines, payment processors.
- **Insurance and claims** — policy lifecycle, claims adjudication where every decision must be defensible years later.
- **Logistics and supply chain** — shipment workflows where every scan, exception, and handoff is itself the data.
- **Healthcare records** — EHR systems where regulators care what the chart said at a given moment.
- **Gaming / collaborative editing** — multiplayer state where deterministic replay is required for debugging or rollback.

### Ecosystem and tooling

- **Dedicated event stores**: KurrentDB (formerly EventStoreDB), AxonServer.
- **Library / framework on top of a relational DB**: Marten (Postgres, .NET), Eventuous (.NET), Axon Framework (JVM), Commanded (Elixir/Postgres), EventFlow (.NET), `node-event-storage`.
- **Roll-your-own substrates**: Postgres with a single `events` table plus a unique index on `(stream_id, version)`; DynamoDB with a conditional `PutItem`; MongoDB with a versioned document per stream.
- **For projections**: a SQL view in the same Postgres database is the simplest; Kafka Connect or Debezium pipes events to a downstream warehouse; Redis or Elasticsearch for low-latency reads.
- **For schema evolution**: JSON Schema, Avro, Protobuf, plus hand-written upcasters; Marten supports `EventMappings` with version tags.

## When

### When the topic emerged and why

The idea predates the name. Double-entry bookkeeping (15th century) is event sourcing on paper. Database write-ahead logs and journaling filesystems use the same trick internally. The pattern was formalised for application code around 2005–2006 by Greg Young in the DDD community, partly as a reaction to the ORM-driven loss of information that came with row-overwrite CRUD. The mid-2010s rise of Kafka popularised "log as source of truth" thinking and made event sourcing more approachable: people had log-shaped infrastructure already.

### When to use it in a project

Reach for it when:

- The audit trail is a hard requirement, not a nice-to-have.
- Domain experts naturally describe the system as a series of business events ("when a shipment is dispatched…", "when a claim is settled…").
- You expect to add new read models or analytics later and want to backfill them from history without a separate ETL pipeline.
- Reproducing production bugs by replaying real events would be high value.
- Multiple consumers care about *why* state changed, not just the current state.

### When NOT to use it

Avoid it when:

- The domain is fundamentally CRUD: admin screens, user preferences, CMS-style content.
- You have hard "right to be forgotten" requirements and no appetite for crypto-shredding or forgettable-payload patterns (see Failure Modes).
- Your team is unfamiliar with DDD, async messaging, and eventual consistency. The pattern punishes shallow adoption.
- The data has no historical value and disk is already a cost concern — events accumulate forever by default.

## How

### How it works under the hood

The lifecycle of a command, using the shopping-cart example from the overview:

1. **Load.** The command handler receives `RemoveItem { cartId: 7, sku: B }`. It calls `eventStore.readStream("cart-7")` and gets the ordered events `[ItemAdded A, ItemAdded B]` at versions 0 and 1.
2. **Fold.** It rehydrates a `Cart` aggregate by applying each event to an empty instance: `cart.apply(e)` for each `e`. The aggregate is now in memory at **stream version 1**.
3. **Decide.** The command handler invokes `cart.removeItem("B")`. The aggregate validates the invariant (the item exists) and produces a new event `ItemRemoved { sku: B }` — it does *not* mutate state yet, only records the proposed event.
4. **Append.** The handler calls `eventStore.appendToStream("cart-7", expectedVersion: 1, [ItemRemoved B])`. The store performs a conditional insert: it accepts only if the current head of `cart-7` is still at version 1.
5. **Conflict or commit.** If another writer raced and appended first, the head is now version 2 and the store rejects with `WrongExpectedVersionException` (KurrentDB) or `ConcurrencyException` (Marten). The handler retries the load-decide-append cycle. If accepted, the new event is at version 2 and gets a fresh global position.
6. **Propagate.** A **subscription** tails the store from its last known global position and pushes the new event to projection handlers. The `CartItemsView` projection updates `cart_items_read.cart_7` to `[A]`. Other consumers (search index, analytics, integration outbox) receive the same event independently.
7. **Acknowledge.** Each projection persists a checkpoint (the global position it has processed up to) so it can resume after a crash without losing or double-counting events.

Internally, an event store is just an append-only file or table. A typical Postgres-based implementation uses one row per event:

```
events(global_position BIGSERIAL, stream_id TEXT, version INT,
       event_type TEXT, payload JSONB, metadata JSONB, occurred_at TIMESTAMPTZ,
       UNIQUE(stream_id, version))
```

The unique index on `(stream_id, version)` is what gives you optimistic concurrency for free — two competing inserts at the same `(stream_id, version)` cannot both succeed.

**Snapshots.** When a stream gets long (say, an account with 50k events), replay-from-zero becomes a latency problem. The fix: periodically write a snapshot of the aggregate state and the version it represents. On load, fetch the latest snapshot, then read events with `version > snapshot.version`. Common triggers: every N events, every N minutes, or only when measured replay time exceeds a threshold. Snapshots must always be rebuildable; never let them become the source of truth.

**Projections.** Two flavours: **inline** (updated in the same transaction as the append, strongly consistent, only works when the projection lives in the same database — Marten supports this in Postgres) and **async** (updated by a subscription, eventually consistent, scales to external stores). Async is the default and the source of the eventual-consistency tax.

**Idempotency.** Subscriptions deliver at-least-once. The same event can arrive twice after a crash or rebalance. Projections handle this by storing the last processed `global_position` per projection and skipping any event with a position less than or equal to that checkpoint, all in the same DB transaction as the projection update. Naturally idempotent updates (`SET status = 'shipped'`) are also fine — duplicate writes are no-ops.

**Schema evolution.** Five common tactics, roughly ordered by complexity:

1. **Additive only** — only add optional fields; old consumers ignore them. Cheapest, works for most changes.
2. **Versioned event types** — `OrderPlacedV2`. Old handlers stay; a new handler is added for V2.
3. **Weak schema** — deserialise into a tolerant shape (dictionary, dynamic) and let the aggregate cope.
4. **Upcasting** — when reading, transform `OrderPlacedV1 → V2 → V3` on the fly so the aggregate only knows V3. Chained upcasters keep the live code clean.
5. **Copy-and-transform** — rewrite the whole stream into a new stream with new event shapes. Expensive, last resort, breaks global ordering with prior history.

### Key trade-offs

| Design choice | What you gain | What you give up |
| --- | --- | --- |
| Append-only log as source of truth | Full history, replay, temporal queries, free audit | Storage grows forever; harder GDPR deletion |
| Async projections | Read scalability, multiple read models, independent failure domains | Eventual consistency; user-visible read-your-write lag |
| Optimistic concurrency on stream version | Lock-free writes, no global locking, high throughput per stream | Conflict retries on hot streams; need careful aggregate boundaries |
| Snapshots | Fast aggregate load on long streams | Extra storage; snapshots can drift if upcasters change semantics |
| Per-aggregate stream | Natural concurrency boundary; clear consistency unit | Cross-aggregate invariants need sagas/process managers, not transactions |
| Domain events as the schema | Business-meaningful history; new read models are cheap | Schema evolution is forever — you can never "drop a column" |

### Common failure modes

- **CRUD events.** Emitting `CartUpdated { state: ... }` instead of `ItemAdded` / `ItemRemoved`. You get all of the cost of event sourcing and none of the benefits — the history carries no meaning.
- **Fat events.** Stuffing entire denormalised payloads into events so projections "don't need joins". Schema evolution becomes a nightmare and PII leaks everywhere.
- **Replay cliff.** A read model rebuild on a 200M-event store takes 14 hours and blocks a deploy. Cause: no checkpointing, no parallel projection workers, no snapshots.
- **Hot stream contention.** A single aggregate (a popular product, a global counter) receives concurrent commands and every write loses the optimistic race. Cause: aggregate boundary modelled too coarsely.
- **Projection drift.** A projection's code was changed but the projection wasn't rebuilt — the read model now disagrees with the events. Cause: no rebuild discipline, no versioned projection name.
- **Lossy upcaster.** Upcasting `V1 → V2` invents a default value for a new field; later business logic assumes that value is real. Cause: treating upcasting as a free lunch.
- **GDPR collision.** A "delete this user" request arrives and the user's email is embedded in 40k events across 8 streams. Mitigations: store PII outside the event store and reference by id (*forgettable payloads*), or encrypt per-user fields with a key you can shred (*crypto-shredding*). Note that EU regulators have argued encrypted personal data is still personal data, so crypto-shredding is not unambiguously compliant.
- **Broker confused with store.** Treating Kafka topic retention as the source of truth, then losing data when retention expires. Kafka is transport; an event store is durable history.

## Why

### Why it exists

Three forces. First, **information loss**: row-overwrite CRUD throws away exactly the data — the history of change — that businesses increasingly care about. Second, **separation of concerns**: write-side invariants and read-side query shapes have different lifecycles and different scaling profiles, and stuffing both into one normalised schema makes both worse. Third, **debuggability**: distributed systems are easier to reason about when state is a function of an ordered input than when it is a graph of mutually-updating rows.

### Why it looks the way it does

The obvious alternative — "keep a history table next to your tables" — fails because the history is *derived* from the writes, not the source of them. The moment the application has a bug, the live table and the history table diverge silently. Event sourcing inverts this: the history *is* the write, and the table is derived. Everything downstream — projections, snapshots, audit, replay — falls out of this inversion. Optimistic concurrency on stream version, rather than pessimistic locking on a row, is the second non-obvious choice: it lets you scale writes horizontally per-aggregate while still giving each aggregate strict serialisability, which is the consistency model the domain actually needs.

### Why it matters now

In 2026 the pattern is mainstream-but-not-default. KurrentDB (the rebranded EventStoreDB, with v24.10+ now its current line) and Marten 7 on Postgres are both stable, well-documented options for .NET teams. Regulatory pressure (PSD3, DORA, evolving AI-act audit requirements) is making "what did the system know, and when" a first-class engineering requirement in finance and healthcare. At the same time, the rise of LLM-assisted debugging makes a replayable event log unusually valuable — agents can re-run scenarios deterministically. Event sourcing is not winning the CRUD market and never will, but it is firmly established for the domains where history is the product.

## Open questions / things to verify in practice

- How long does a cold rebuild of your largest projection actually take, end-to-end, on real hardware? Measure before you need it.
- What is your snapshot trigger, and does it pay for itself? Benchmark replay-with-snapshot vs replay-without on a representative stream.
- How do you express a cross-aggregate invariant that a transaction would have given you for free in CRUD? Pick a real one and design the saga.
- What is your concrete schema-evolution playbook? Pick three plausible changes (new field, renamed event, split aggregate) and walk through them on paper before you need to in production.
- How do you handle a PII deletion request? Decide between forgettable payloads and crypto-shredding *before* you accept PII into an event.
- How does a user perceive the eventual-consistency gap between command and read model? Measure p99 lag and decide whether to mask it in the UI (optimistic update) or wait for it (synchronous read-your-write on the command response).
