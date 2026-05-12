using Domain.Accounts;

namespace Application.Abstractions;

// The minimal event-store contract from deep-dive §What — append and read.
// Two primitives are enough to demonstrate the pattern; everything else
// (subscriptions, snapshots, projections) is composed on top.
public interface IEventStore
{
    // expectedVersion is the version the caller saw when it loaded the
    // stream. The store accepts the append ONLY if the current head still
    // matches. -1 means "the stream must not yet exist". This single check
    // is what gives event sourcing optimistic concurrency for free
    // (deep-dive §How step 5).
    Task Append(string streamId, int expectedVersion, IReadOnlyList<IDomainEvent> events, CancellationToken ct);

    // Read the full stream from version 0. A real store would page; for the
    // demo we materialise it.
    Task<IReadOnlyList<IDomainEvent>> Read(string streamId, CancellationToken ct);

    // Subscribe a handler to every new event appended after this call.
    // This is what feeds projections (deep-dive §How step 6).
    void Subscribe(Func<string, IDomainEvent, Task> onAppended);
}

// Distinct from DomainException — this is a *store* concern, raised when
// the optimistic check fails. The caller's correct response is "reload,
// re-decide, re-append" (deep-dive §How step 5).
public sealed class ConcurrencyException(string streamId, int expected, int actual)
    : Exception($"Concurrency conflict on stream '{streamId}': expected version {expected}, but stream is at version {actual}.")
{
    public string StreamId { get; } = streamId;
    public int Expected { get; } = expected;
    public int Actual { get; } = actual;
}
