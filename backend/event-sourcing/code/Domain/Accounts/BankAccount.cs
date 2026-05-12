namespace Domain.Accounts;

// The aggregate. State is NEVER stored — it is rebuilt every time by folding
// the event stream (deep-dive §How, step 2 "Fold"). The public surface is
// deliberately tiny:
//   - Static factory `Open(...)` for creating a brand-new aggregate.
//     This returns an instance whose only "uncommitted" event is AccountOpened.
//   - Static factory `Rehydrate(events)` for loading from the event store.
//     This replays history without producing new uncommitted events.
//   - Behavioural methods (`Deposit`, `Withdraw`) that validate invariants
//     and emit new events.
//   - `DequeueUncommittedEvents()` so the command handler can hand them to
//     the event store.
public sealed class BankAccount
{
    // The list of new events produced during this in-memory session.
    // These are NOT yet persisted — the handler appends them.
    private readonly List<IDomainEvent> _uncommitted = [];

    // Current folded state. Private setters — only `Apply` mutates them.
    public Guid Id { get; private set; }
    public string Owner { get; private set; } = "";
    public decimal Balance { get; private set; }

    // The stream version of the last applied event. Starts at -1 to mean
    // "nothing applied yet"; AccountOpened brings it to 0; each subsequent
    // event increments by 1. This is the value we hand to the store as
    // `expectedVersion` for optimistic concurrency (deep-dive §How step 4).
    public int Version { get; private set; } = -1;

    // Private ctor — the only way to obtain an instance is via the factories
    // below. That keeps construction-paths explicit.
    private BankAccount() { }

    // Brand-new account. Note we emit the event THEN apply it through the
    // same path used during rehydration. This guarantees the "apply" logic
    // is the single source of truth for state transitions.
    public static BankAccount Open(Guid id, string owner, decimal openingBalance)
    {
        if (openingBalance < 0)
            throw new DomainException("Opening balance cannot be negative.");

        var account = new BankAccount();
        account.Raise(new AccountOpened(id, owner, openingBalance));
        return account;
    }

    // Replay path. Same `Apply` method is used; the only difference is we
    // do NOT add the event to `_uncommitted` because it already lives in
    // the store. After this loop, `Version` matches the last persisted
    // event's stream version, which the handler will pass back to the
    // store as `expectedVersion`.
    public static BankAccount Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var account = new BankAccount();
        foreach (var e in history) account.Apply(e);
        return account;
    }

    // Business decision: deposit. Validates, raises the event. Note that
    // Balance is NOT updated here directly — the event flows through Apply.
    public void Deposit(decimal amount)
    {
        if (amount <= 0) throw new DomainException("Deposit amount must be positive.");
        Raise(new MoneyDeposited(amount));
    }

    // Business decision: withdraw. The invariant (no overdraft) is checked
    // against current folded state. If we ran on stale state because of a
    // concurrent commit, the store's optimistic check (deep-dive §How step 5)
    // catches the race at append time.
    public void Withdraw(decimal amount)
    {
        if (amount <= 0) throw new DomainException("Withdrawal amount must be positive.");
        if (amount > Balance) throw new DomainException($"Insufficient funds: balance={Balance}, requested={amount}.");
        Raise(new MoneyWithdrawn(amount));
    }

    // Caller takes ownership of the produced events and clears the list.
    // After this call, the aggregate has no pending writes.
    public IReadOnlyList<IDomainEvent> DequeueUncommittedEvents()
    {
        var copy = _uncommitted.ToArray();
        _uncommitted.Clear();
        return copy;
    }

    // Internal helper: apply + remember (for new events only).
    private void Raise(IDomainEvent e)
    {
        Apply(e);
        _uncommitted.Add(e);
    }

    // The fold function. One case per event type. THIS is the heart of
    // event sourcing — every state change in the system flows through here,
    // whether the event was just produced or replayed from 2018.
    private void Apply(IDomainEvent e)
    {
        switch (e)
        {
            case AccountOpened o:
                Id = o.AccountId;
                Owner = o.Owner;
                Balance = o.OpeningBalance;
                break;
            case MoneyDeposited d:
                Balance += d.Amount;
                break;
            case MoneyWithdrawn w:
                Balance -= w.Amount;
                break;
            default:
                throw new DomainException($"Unknown event type: {e.GetType().Name}");
        }
        Version++;
    }
}
