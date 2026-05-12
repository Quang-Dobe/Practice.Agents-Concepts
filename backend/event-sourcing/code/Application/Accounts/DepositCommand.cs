using Application.Abstractions;
using Application.Mediator;
using Domain.Accounts;

namespace Application.Accounts;

// ExpectedVersion is OPTIONAL on the command. When null we trust the version
// we just loaded; when supplied we use the caller's value so the demo can
// deliberately submit a stale version and observe the conflict.
public sealed record DepositCommand(Guid AccountId, decimal Amount, int? ExpectedVersion = null)
    : IRequest<int>;

public sealed class DepositHandler(IEventStore store)
    : IRequestHandler<DepositCommand, int>
{
    public async Task<int> Handle(DepositCommand cmd, CancellationToken ct)
    {
        var streamId = OpenAccountHandler.StreamId(cmd.AccountId);

        // 1. Load — read the full stream.  2. Fold — rehydrate the aggregate.
        // This is the canonical event-sourcing load path (deep-dive §How 1+2).
        var history = await store.Read(streamId, ct);
        var account = BankAccount.Rehydrate(history);

        // 3. Decide — the aggregate validates the invariant and emits an event.
        account.Deposit(cmd.Amount);

        // 4. Append — pass back the version we loaded as expectedVersion.
        // The store's unique (streamId, version) constraint enforces serialisability.
        var expected = cmd.ExpectedVersion ?? account.Version - 1;  // -1 because Deposit() already bumped version
        var events = account.DequeueUncommittedEvents();
        await store.Append(streamId, expected, events, ct);
        return account.Version;
    }
}
