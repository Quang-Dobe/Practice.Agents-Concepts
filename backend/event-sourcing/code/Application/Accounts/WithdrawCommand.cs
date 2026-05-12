using Application.Abstractions;
using Application.Mediator;
using Domain.Accounts;

namespace Application.Accounts;

public sealed record WithdrawCommand(Guid AccountId, decimal Amount, int? ExpectedVersion = null)
    : IRequest<int>;

public sealed class WithdrawHandler(IEventStore store)
    : IRequestHandler<WithdrawCommand, int>
{
    public async Task<int> Handle(WithdrawCommand cmd, CancellationToken ct)
    {
        var streamId = OpenAccountHandler.StreamId(cmd.AccountId);

        // Same load-fold-decide-append cycle as deposit. The overdraft
        // invariant is enforced in BankAccount.Withdraw, against the
        // FOLDED balance, which is the only correct view of state.
        var history = await store.Read(streamId, ct);
        var account = BankAccount.Rehydrate(history);
        account.Withdraw(cmd.Amount);

        var expected = cmd.ExpectedVersion ?? account.Version - 1;
        var events = account.DequeueUncommittedEvents();
        await store.Append(streamId, expected, events, ct);
        return account.Version;
    }
}
