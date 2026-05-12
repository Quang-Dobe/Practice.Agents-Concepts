# Event Sourcing — Overview

> Instead of storing what your data currently *is*, store every change that ever happened to it — and recompute the current state by replaying those changes in order.

## The 30-second version

In a normal CRUD system, your `orders` table holds one row per order, and `UPDATE` overwrites the old values. The history is gone the moment you write. Event sourcing flips this: the source of truth becomes an append-only log of facts (`OrderPlaced`, `ItemAdded`, `OrderPaid`, `OrderShipped`), and the "current state" of an order is a function you compute by folding those events together. Engineers reach for it when *how* a piece of data got to its current shape matters as much as the shape itself — audits, financial ledgers, regulated workflows, complex domain logic where bugs are best diagnosed by replay.

## The mental model

Think of your bank account. Your bank does not store a single row that says `balance: $1,432.18` and overwrite it every transaction. It stores a ledger: `+$2000 salary`, `-$45 groceries`, `-$522.82 rent`, `+$0.00 interest`. The balance is just `sum(ledger)`. If a transaction is disputed, the bank does not edit the past — it appends a *correcting* entry (`-$45 reversal`). Yesterday's balance is still recoverable by replaying entries up to yesterday's date. Next month's audit is trivial because nothing was ever overwritten.

Event sourcing applies that bank-ledger discipline to *any* entity in your system. An `Order` is no longer a row; it's a stream of events. The "row" representation you query against is a **projection** — a cached fold of the stream, rebuildable from scratch any time.

Concrete worked example. A shopping cart in CRUD:

```
UPDATE cart SET items = '[A,B]' WHERE id = 7;   -- user adds A, then B
UPDATE cart SET items = '[A]'   WHERE id = 7;   -- user removes B
```

After this, you know the cart contains `[A]`. You cannot answer "did the user briefly consider B?" — that fact was overwritten. Event-sourced:

```
event 1: ItemAdded   { cart: 7, sku: A }
event 2: ItemAdded   { cart: 7, sku: B }
event 3: ItemRemoved { cart: 7, sku: B }
```

Folding gives you `[A]` as the current state. But you can also answer "what got abandoned?" — which is gold for product analytics, fraud detection, and recommendation engines.

## What it is NOT

- **Not an audit log bolted onto CRUD.** An audit log is a *derived* sidecar; in event sourcing, events *are* the database.
- **Not CQRS.** CQRS separates read and write models; it pairs well with event sourcing but is a distinct idea.
- **Not message queues / Kafka-as-a-bus.** A broker moves events between services; an event store is your durable source of truth for one service's state.
- **Not "just append rows and never delete."** Soft deletes do not give you replay, projections, or temporal queries.

## When you would reach for it

- Financial ledgers, billing, double-entry accounting — where every state change must be defensible.
- Regulated domains (healthcare, insurance, trading) where auditors ask "what did the system know at 14:02?"
- Complex domain logic with rich behavior over time — order workflows, claims processing, multi-step approvals.
- Systems where you want to add new read models later without backfilling — rebuild a projection from history.
- Debugging and reproducing production bugs by replaying a real event stream.

## When you would NOT reach for it

- Simple CRUD admin screens, settings pages, content management — the complexity tax is not worth it.
- Hard "right to be forgotten" deletion requirements without a crypto-shredding strategy.
- Teams new to DDD and distributed systems — the learning curve is steep and missteps are expensive.
- Low-stakes data where history has no business value.

## Key vocabulary (just enough to keep reading)

- **Event** — an immutable, past-tense fact (`OrderPlaced`), never a command or intent.
- **Event store** — the append-only database that holds events, ordered per entity.
- **Stream** — the ordered sequence of events for one entity (one order, one account).
- **Aggregate** — the domain object whose state is rebuilt by folding its stream.
- **Projection / read model** — a materialized view derived from events, optimized for queries.
- **Replay** — recomputing state or a projection by reading events from the start.
- **Snapshot** — a cached state at version N so replay does not always start from event 1.
- **CQRS** — Command Query Responsibility Segregation; a common companion pattern, not the same thing.
- **Upcaster / event versioning** — code that migrates old event shapes forward as your schema evolves.

## What's next

The next document (`02-deep-dive.md`) answers What / Where / When / How / Why in detail: event store internals, aggregate design, concurrency via optimistic versioning, projections and CQRS pairing, snapshotting, and event schema evolution.
