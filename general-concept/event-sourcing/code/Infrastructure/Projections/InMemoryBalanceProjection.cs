using Application.Abstractions;
using Domain.Accounts;

namespace Infrastructure.Projections;

// A trivial read model. Each event flows in via Subscribe() on the store
// and the projection folds them into a dictionary keyed by account id.
// In production this would be a row in a Postgres table updated by an
// async subscription with a checkpoint (deep-dive §How step 6/7,
// practice doc §4 idempotency).
//
// Notice: the projection does NOT know about the aggregate. It only knows
// the event shapes. That decoupling is what lets you spin up new read
// models months later by replaying the same log (practice doc §11).
public sealed class InMemoryBalanceProjection : IBalanceProjection
{
    private readonly Dictionary<Guid, decimal> _balances = new();
    private readonly object _gate = new();

    // Wired in Program.cs via IEventStore.Subscribe.
    public Task On(string streamId, IDomainEvent e)
    {
        lock (_gate)
        {
            switch (e)
            {
                case AccountOpened o:
                    _balances[o.AccountId] = o.OpeningBalance;
                    break;
                case MoneyDeposited d:
                    _balances[ExtractId(streamId)] += d.Amount;
                    break;
                case MoneyWithdrawn w:
                    _balances[ExtractId(streamId)] -= w.Amount;
                    break;
            }
        }
        return Task.CompletedTask;
    }

    public decimal? GetBalance(Guid accountId)
    {
        lock (_gate)
            return _balances.TryGetValue(accountId, out var b) ? b : null;
    }

    // We chose to put the account id in the AccountOpened event itself, so
    // we could read it from there for that case. For deposits/withdrawals
    // we recover it from the stream id ("account-{guid}") because keeping
    // events minimal beats stuffing redundant ids everywhere.
    private static Guid ExtractId(string streamId) => Guid.Parse(streamId.Substring("account-".Length));
}
