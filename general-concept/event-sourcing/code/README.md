# Event Sourcing — MVP Code

The smallest runnable demo of event sourcing: a `BankAccount` aggregate, an
in-memory event store with optimistic concurrency, a subscribed read-model
projection, and a console scenario that proves state is *derived* from
events.

## What it demonstrates

- **Aggregate state is a fold over events.** `BankAccount.Rehydrate(events)` replays the stream into a fresh instance; we never store balance directly (`Domain/Accounts/BankAccount.cs`).
- **Optimistic concurrency via stream version.** `InMemoryEventStore.Append` accepts only when `expectedVersion` matches the head — the same `UNIQUE(stream_id, version)` trick the deep dive describes (`Infrastructure/EventStore/InMemoryEventStore.cs`).
- **CQRS read side built from the same log.** A subscription feeds `InMemoryBalanceProjection`, queried via `GetBalanceQuery` (`Infrastructure/Projections/InMemoryBalanceProjection.cs`).
- **Conflict detection on stale writes.** `Program.cs` step 5 submits a deposit with `ExpectedVersion: 0` and the store rejects it with `ConcurrencyException`.

## Prerequisites

- .NET 8 SDK (`dotnet --version` → `8.0.x` or newer).
- No external services. Everything is in-memory.

## Run it

```bash
dotnet run --project Console/ConsoleApp.csproj
```

## Expected output

```
Account id: <guid>

Projection balance (read model):     120
Aggregate balance (rehydrated fold): 120
Stream version after rehydration:    2

Event stream (source of truth):
  v0: AccountOpened { ... OpeningBalance = 100 }
  v1: MoneyDeposited { Amount = 50 }
  v2: MoneyWithdrawn { Amount = 30 }

Concurrency conflict (as expected): ...expected version 0, but stream is at version 2.

Final balance after rejected write:  120
```

## What to try next

- Add a `MoneyTransferred` event and a new handler — observe how the existing projection still works because it ignores unknown events.
- Comment out `store.Subscribe(projection.On)` in `Program.cs` and watch the projection balance go null while the rehydrated aggregate still computes 120.
- Make `BankAccount.Withdraw` request more than the balance — see the `DomainException` thrown *before* any event is appended.
- Change `OpeningBalance` in `OpenAccountCommand` to negative and confirm the domain invariant trips inside `BankAccount.Open`.
