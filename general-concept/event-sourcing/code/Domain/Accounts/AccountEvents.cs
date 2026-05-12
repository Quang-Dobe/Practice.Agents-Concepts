namespace Domain.Accounts;

// Every event is a past-tense, immutable business fact (deep-dive §What).
// Records give us value-equality and `with`-expression copies for free.
// IDomainEvent is just a marker — we do not stuff metadata on it; the store
// is responsible for stream id + version + global position. The event itself
// only carries domain payload.
public interface IDomainEvent { }

// Account is opened with an opening balance. Past tense, named for the
// business intent rather than "AccountCreated" — see practice doc §2.
public sealed record AccountOpened(Guid AccountId, string Owner, decimal OpeningBalance) : IDomainEvent;

// One business decision = one event (practice doc §3). We do NOT carry the
// "balance after" here — balance is *derived* from folding events. That is
// the whole point of event sourcing.
public sealed record MoneyDeposited(decimal Amount) : IDomainEvent;

public sealed record MoneyWithdrawn(decimal Amount) : IDomainEvent;
