// Composition root + the demo scenario. Five things happen here, each
// matching a section of the deep-dive doc:
//
//   1. Open an account, deposit, withdraw — events are appended.
//   2. The async projection (subscribed to the store) folds those events
//      into a read model. We query it via the GetBalanceQuery.
//   3. We rehydrate a FRESH aggregate from the store and confirm its
//      derived balance matches — proving state is computed, not stored.
//   4. We dump the raw event stream to make it visible that the "row" is
//      a sequence of facts.
//   5. We force a concurrency conflict by submitting a deposit with a
//      deliberately stale ExpectedVersion and observe the rejection.

using Application.Abstractions;
using Application.Accounts;
using Application.Mediator;
using Domain.Accounts;
using Infrastructure.EventStore;
using Infrastructure.Projections;
using Microsoft.Extensions.DependencyInjection;

// ---- DI wiring ----------------------------------------------------------
var services = new ServiceCollection();

// Store and projection are singletons because they hold state for the
// process lifetime (an in-memory store would otherwise reset per scope).
var store = new InMemoryEventStore();
var projection = new InMemoryBalanceProjection();
store.Subscribe(projection.On);  // the subscription that drives the read model

services.AddSingleton<IEventStore>(store);
services.AddSingleton<IBalanceProjection>(projection);

// Custom hand-rolled mediator (no MediatR). Scans the Application
// assembly for IRequestHandler<,> implementations and registers them.
services.AddCustomMediator(typeof(OpenAccountCommand).Assembly);

await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

// ---- Scenario -----------------------------------------------------------
var accountId = Guid.NewGuid();
Console.WriteLine($"Account id: {accountId}\n");

// Step 1 — write side. Three commands, three appends.
await mediator.Send(new OpenAccountCommand(accountId, "Alice", 100m));
await mediator.Send(new DepositCommand(accountId, 50m));
await mediator.Send(new WithdrawCommand(accountId, 30m));

// Step 2 — read side. Query the projection that was updated by the
// subscription. This is the CQRS pairing in miniature.
var projectedBalance = await mediator.Send(new GetBalanceQuery(accountId));
Console.WriteLine($"Projection balance (read model):     {projectedBalance}");

// Step 3 — the load-fold proof. We rehydrate a brand-new aggregate from
// the event store and check the derived balance. If event sourcing is
// working, these two numbers MUST agree, because both are functions of
// the same stream.
var stream = await store.Read($"account-{accountId}", CancellationToken.None);
var rehydrated = BankAccount.Rehydrate(stream);
Console.WriteLine($"Aggregate balance (rehydrated fold): {rehydrated.Balance}");
Console.WriteLine($"Stream version after rehydration:    {rehydrated.Version}\n");

// Step 4 — show the raw history. This is the source of truth; everything
// else (balance, projection, snapshots) is a derived view of these rows.
Console.WriteLine("Event stream (source of truth):");
for (var i = 0; i < stream.Count; i++)
    Console.WriteLine($"  v{i}: {stream[i]}");
Console.WriteLine();

// Step 5 — optimistic concurrency. We deliberately submit a deposit with
// an ExpectedVersion that we KNOW is stale (we say "I think the head is
// still at version 0" when it is actually at version 2). The store rejects
// the append. In a real handler the response is "reload, re-decide,
// re-append" (deep-dive §How step 5).
try
{
    await mediator.Send(new DepositCommand(accountId, 999m, ExpectedVersion: 0));
    Console.WriteLine("ERROR: stale write was accepted — this should not happen.");
}
catch (ConcurrencyException ex)
{
    Console.WriteLine($"Concurrency conflict (as expected): {ex.Message}");
}

// Final sanity check — balance unchanged by the rejected write.
var finalBalance = await mediator.Send(new GetBalanceQuery(accountId));
Console.WriteLine($"\nFinal balance after rejected write:  {finalBalance}");
