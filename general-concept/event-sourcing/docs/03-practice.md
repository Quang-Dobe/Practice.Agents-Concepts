# Event Sourcing — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical fintech back-end, event sourcing is the persistence layer behind whatever owns money movement — wallet, ledger, payouts, settlement. The rest of the system (notifications, statements, fraud, reporting) lives downstream as projections off that one log. When a regulator asks "what did the balance look like at 14:02 on the 8th," the answer is `replay until timestamp`, not a forensic SQL hunt.

In e-commerce and logistics, you find it on the order/shipment aggregate — the thing that has a long lifecycle, many actors, and external systems mutating it asynchronously (warehouses, carriers, customer service). The order is not a row; it is a story. CRUD storage of an order is what produces "the status is `delivered` but the customer says it never arrived" tickets.

In claims, policy, and EHR systems, event sourcing is the thing the compliance team will eventually force on you if you don't pick it on day one. Anywhere "who knew what, when" is legally load-bearing, you will end up with an append-only log; the only question is whether you designed for it or bolted it on.

In most other places — admin panels, settings, CMS — you will *not* meet event sourcing, and if you do, someone has overreached.

## Best practices

### 1. Pick the aggregates that justify the cost, not the service
**Do:** Event-source the 1–3 aggregates where history is the business (Order, Wallet, Policy). Keep `User`, `Settings`, `ProductCatalog` as boring CRUD in the same service.
**Why:** The complexity tax (projections, eventual consistency, schema evolution) compounds per aggregate. Spending it on a `UserPreferences` aggregate pays nothing back and slows every feature.
**Avoid:** "We're an event-sourced shop" — making every entity a stream because the team adopted the pattern.

### 2. Name events as past-tense business facts, not state diffs
**Do:** Emit `OrderShipped`, `PaymentCaptured`, `AddressCorrected`. The name should make sense to a domain expert with no code context.
**Why:** Generic events like `OrderUpdated { fields }` or `OrderStatusChanged { from, to }` are CRUD-in-disguise — they carry no intent, so projections can't react meaningfully, analytics can't query them, and the history reads like a diff log.
**Avoid:** `EntityCreated`, `EntityUpdated`, `EntityDeleted`. If your event names look like HTTP verbs, you have audited CRUD, not event sourcing.

### 3. Decide event granularity around invariants and consumers
**Do:** One event per business decision. If shipping requires picking a carrier, allocating stock, and printing a label, that may be one `OrderShipped` event with those fields, or three events — pick based on which downstream consumer needs to react to which step.
**Why:** Events too fine-grained turn every command into a 10-event burst and force projections to reassemble state. Too coarse, and you can't add a new consumer without re-deriving from payloads.
**Avoid:** Splitting events to mirror UI form fields. The form is not the domain.

### 4. Make every projection and integration handler idempotent
**Do:** Store the last processed `global_position` per projection in the same transaction as the projection update. Use upserts keyed on event id for side-effects.
**Why:** Subscriptions are at-least-once. A pod restart mid-batch will redeliver the last events. Without idempotency, you double-charge cards, double-send emails, or double-count inventory — and you will only find out from customers.
**Avoid:** Trusting "we haven't seen a duplicate yet" — the next deploy will give you one.

### 5. Treat snapshots as a cache, never a source of truth
**Do:** Snapshot on a measurable trigger (e.g. every 200 events, or when warm-load p95 exceeds 50ms). Version the snapshot schema. Be able to delete every snapshot and have the system still work.
**Why:** A snapshot written by yesterday's aggregate code, replayed against today's invariants, produces silently wrong state. If snapshots are load-bearing, an aggregate refactor becomes a data migration.
**Avoid:** Snapshot-by-default from day one. Most aggregates never get long enough to need them, and premature snapshotting just adds a stale cache layer.

### 6. Pick a schema-evolution strategy *before* your second deploy
**Do:** Default to additive changes (new optional fields). Add upcasters when shape changes. Reserve copy-and-transform for last-resort renames. Keep upcasters pure and chained: `V1 → V2 → V3`, never `V1 → V3` directly.
**Why:** Events are forever. Six months in, you will have 50M events with three event versions in the wild, and "just run a migration" stops being an option.
**Avoid:** Mutating old events in place. The moment you do this you've lost the one property the whole pattern is built on.

### 7. Plan for GDPR / "right to be forgotten" before accepting PII
**Do:** Either keep PII out of the event entirely (store a `customerId`, look up the name from a deletable side-table — the *forgettable payload* pattern), or encrypt PII per-subject with a key you can shred. Decide which, per aggregate, in design review.
**Why:** Once `email`, `address`, or `nationalId` is sprayed across 8 streams and 40k events, a single deletion request becomes an engineering project. Note: EU regulators have argued that encrypted personal data is still personal data, so crypto-shredding is a defensible practice but not a guaranteed legal shield — talk to your DPO.
**Avoid:** Putting full customer profiles into event payloads "because it makes projections easier."

### 8. Project asynchronously, monitor lag, but design the UX for it
**Do:** Default to async projections. Expose projection lag (in events and in seconds) as a first-class metric. For commands where the user expects to see their own change immediately, return the new state from the command handler or use an optimistic UI update.
**Why:** Eventual consistency is fine for analytics, terrible for "I just placed an order and the order list is empty." The lag is rarely the problem — the surprise is.
**Avoid:** Forcing inline projections everywhere to dodge the UX problem. You lose the scale-out story and recreate distributed-transaction pain.

### 9. Choose the store that matches your operational reality
**Do:** For .NET teams already on Postgres, **Marten** is the boring-and-correct default — same DB as the rest of the app, inline projections available, no new ops surface. For polyglot orgs or very high write throughput on long streams, **KurrentDB** (formerly EventStoreDB) earns its keep with built-in subscriptions and cluster semantics.
**Why:** A dedicated event store you don't operate well is worse than Postgres you already operate well. The bottleneck in practice is the team's ability to debug at 3 a.m., not the store's max appends/sec.
**Avoid:** Using Kafka as the event store. Kafka is transport with retention; it has no per-aggregate optimistic concurrency and no `readStream(streamId)` primitive. You will reinvent both badly.

### 10. Version your projections by name and own their rebuild
**Do:** Name projection tables/views with a version suffix (`orders_read_v3`). To change a projection, deploy v4 alongside v3, rebuild v4 from event position 0, cut reads over, drop v3.
**Why:** Mutating a live projection schema in place means your read model briefly disagrees with your events, and "briefly" tends to mean "until someone notices in production."
**Avoid:** Reusing the same projection name across breaking changes. There is no good way to tell partially-rebuilt data apart from fully-rebuilt data after the fact.

### 11. Measure cold rebuild time of your biggest projection, on real hardware
**Do:** Run it. Write the number down. Re-measure quarterly. Parallelise by stream-id partition once it climbs past your deploy-window tolerance.
**Why:** Teams discover their rebuild takes 14 hours during the incident that requires the rebuild. That is the worst time to find out.
**Avoid:** Estimating rebuild time from event count alone — projection cost per event varies by two orders of magnitude.

## Anti-patterns to recognize

- **CRUD-in-disguise events**: Events named `XUpdated` with a full state payload. The log carries no semantics, so nothing downstream can do anything useful with it; rename to business facts and re-derive payloads from intent.
- **Event-sourcing the whole service**: Every entity gets a stream because "consistency." The complexity tax bankrupts feature velocity; keep ES scoped to the aggregates whose history is the product.
- **No versioning plan**: First event-schema change ships as a breaking edit to the existing event type. Old events deserialise wrong forever; introduce versioned types + upcasters before the *second* schema change, not the tenth.
- **Eager inline projections everywhere**: Every command transactionally updates 6 read models. Writes get slow and brittle; coupling returns through the back door. Push everything except read-your-write critical views to async subscriptions.
- **Fat events**: Embedding the full customer record, full product, full address into every event "so projections don't need joins." Schema evolution becomes immovable and PII is everywhere; carry ids, denormalise in the projection.
- **Snapshots as truth**: Snapshots get hand-edited to fix a bug. Now events and state disagree silently forever; snapshots must be rebuildable from events, full stop.
- **Replay cliff**: No checkpointing, no partitioned projection workers, no idea how long a rebuild takes. The day you need a rebuild is the day you discover it takes 11 hours; measure and parallelise before then.
- **Broker-as-store**: Kafka topic with 7-day retention treated as the source of truth. After 8 days, history is gone; the event store and the broker are different jobs, run both.

## Real-world usage patterns

**Fintech ledger (mid-scale, ~500 TPS).** A wallet service per user, each wallet a stream, events like `FundsCredited`, `FundsDebited`, `HoldPlaced`, `HoldReleased`. Balance is a projection; statements are a projection; reconciliation runs nightly by replaying the day's events against a sealed previous-day snapshot. Non-obvious lesson: putting the running balance *inside* the event (`balanceAfter`) sounds redundant but saves you when reconciliation finds a drift — you can pinpoint the first event where the projection diverged.

**Order workflow in retail (multi-region, eventual consistency tolerated).** `Order` is event-sourced, everything else is CRUD. Events drive shipping, billing, fraud, and the customer email pipeline as independent subscriptions. The check-out page uses optimistic UI to mask the 200–800ms projection lag. Lesson: the team that tried to make the order-list page strongly consistent ended up with inline projections, a distributed transaction across two databases, and eventually rolled back to async-plus-optimistic-UI six months later.

**Insurance claims (regulated, low TPS, decade-long retention).** Every claim is a stream that may stay open for years. Snapshots fire every 50 events. PII is in a separate vault keyed by `claimantId`; events carry only the id. Lesson: the actuarial team's "new" report from 2025 was satisfied by writing a projection that replayed history since 2018 — no data warehouse migration, no ETL, just `replay from 0`. This is the moment people stop arguing about whether event sourcing was worth it.

**Multiplayer game state (high TPS per session, short-lived streams).** Each match is a stream, events are player actions, the server replays to reconcile clients. Streams are deleted wholesale 30 days after match end. Lesson: short-lived streams flip several defaults — snapshots are unnecessary, GDPR is easy (delete the stream), but hot-stream concurrency is brutal, so the aggregate boundary is "match" not "player."

## Operational checklist

- **Monitoring**: per-projection lag (events behind, seconds behind), append latency p99, optimistic-concurrency-conflict rate per stream, subscription checkpoint age.
- **Failure handling**: is there a runbook for "projection X is corrupt — rebuild it"? Has it been executed in staging this quarter?
- **Idempotency**: every subscription handler tested with a deliberately replayed event batch.
- **Schema evolution**: versioning convention documented, at least one upcaster shipped, integration test that loads streams containing all historical event versions.
- **PII / GDPR**: decision recorded per aggregate (forgettable payload vs. crypto-shredding vs. no PII), deletion path tested end-to-end.
- **Snapshots**: trigger documented, "delete all snapshots, system still works" verified in CI.
- **Rebuild cost**: cold rebuild time of the largest projection measured on production-sized data; result is within deploy-window tolerance.
- **Store choice**: backup, restore, and point-in-time recovery rehearsed for the actual event store (not assumed because "it's just Postgres").
- **Onboarding**: a new engineer can find, in the repo, an ADR explaining which aggregates are event-sourced and why; can run a local replay; knows the difference between a domain event and an integration event in this codebase.

## How this topic typically evolves in a codebase

Teams start with one event-sourced aggregate, often hand-rolled on Postgres with an `events` table and a unique `(stream_id, version)` index. Projections are inline SQL views or simple tables updated in the same transaction. Life is good for 3–6 months.

The first pain arrives with the first schema change: someone adds a field to an event payload, deploys, and old events deserialise wrong. This forces a versioning convention and the first upcaster. Around the same time, a read model gets slow, and the team builds the first async subscription with a checkpoint table — at which point eventual consistency starts showing up in QA tickets. The team learns to instrument projection lag, retrofits idempotency, and writes a runbook for "rebuild projection X."

The painful migration point usually comes 12–24 months in, when (a) an event needs to be renamed or split, (b) someone files a GDPR request and PII is everywhere, or (c) a cold rebuild is needed in production and nobody knows how long it will take. Teams that planned for versioning, forgettable payloads, and partitioned rebuilds glide through. Teams that didn't will either roll back to CRUD for that aggregate, or spend a quarter on a copy-and-transform migration of the entire log. After that quarter, they take event design seriously — and the pattern starts paying back.

## Further reading

- [Greg Young — "A Decade of DDD, CQRS, Event Sourcing"](https://www.youtube.com/watch?v=LDW0QWie21s) — the originator's retrospective; calibrates expectations on what the pattern is actually good at.
- [Mathias Verraes — "Eventsourcing Patterns: Crypto-Shredding"](https://verraes.net/2019/05/eventsourcing-patterns-throw-away-the-key/) — short, precise treatment of the canonical GDPR mitigation.
- [Oskar Dudycz — "How to deal with privacy and GDPR in event-driven systems"](https://event-driven.io/en/gdpr_in_event_driven_architecture/) — the most balanced write-up on forgettable payloads vs. crypto-shredding, including the legal caveats.
- [Marten documentation — Event Store and Projections](https://martendb.io/events/learning.html) — even if you don't use Marten, the docs are the clearest reference for inline vs. async projection trade-offs on Postgres.
- ["Scaling Event Sourcing at Jet"](https://medium.com/@eulerfx/scaling-event-sourcing-at-jet-9c873cac33b8) — concrete numbers and design choices from a real high-volume system; especially good on partitioning projections.
- [Microsoft — Event Sourcing pattern (Azure Architecture Center)](https://learn.microsoft.com/en-us/azure/architecture/patterns/event-sourcing) — neutral, framework-agnostic reference; useful to send to a skeptical reviewer who wants a vendor-neutral source.
