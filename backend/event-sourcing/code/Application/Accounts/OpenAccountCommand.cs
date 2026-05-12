using Application.Abstractions;
using Application.Mediator;
using Domain.Accounts;

namespace Application.Accounts;

// Command = intent (deep-dive §What). The handler turns it into one or
// more events and appends them.
public sealed record OpenAccountCommand(Guid AccountId, string Owner, decimal OpeningBalance)
    : IRequest<int>;  // returns the new stream version

public sealed class OpenAccountHandler(IEventStore store)
    : IRequestHandler<OpenAccountCommand, int>
{
    public async Task<int> Handle(OpenAccountCommand cmd, CancellationToken ct)
    {
        // No load step here — opening creates a brand-new stream. expectedVersion=-1
        // is the optimistic-concurrency way of saying "this stream must NOT exist yet".
        var account = BankAccount.Open(cmd.AccountId, cmd.Owner, cmd.OpeningBalance);
        var events = account.DequeueUncommittedEvents();

        await store.Append(StreamId(cmd.AccountId), expectedVersion: -1, events, ct);
        return account.Version;
    }

    // Convention: one stream per aggregate id (deep-dive §What).
    internal static string StreamId(Guid accountId) => $"account-{accountId}";
}
