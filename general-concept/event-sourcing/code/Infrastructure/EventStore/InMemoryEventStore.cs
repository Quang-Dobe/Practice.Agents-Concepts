using Application.Abstractions;
using Domain.Accounts;

namespace Infrastructure.EventStore;

// The smallest honest event store: a dictionary of stream-id → list-of-events.
// In production this would be a Postgres table with a UNIQUE (stream_id, version)
// index, KurrentDB, or similar (deep-dive §How). The CONTRACT is the same:
//   - Append is conditional on expectedVersion.
//   - Reads return events in stream-version order.
//   - Subscribers are notified after each successful append.
public sealed class InMemoryEventStore : IEventStore
{
    // Per-stream lock so two writers to the SAME stream can't both pass the
    // optimistic check. Different streams write concurrently — that's the
    // whole point of per-aggregate streams (deep-dive §Why).
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new();
    private readonly object _gate = new();
    private readonly List<Func<string, IDomainEvent, Task>> _subscribers = [];

    public Task Append(string streamId, int expectedVersion, IReadOnlyList<IDomainEvent> events, CancellationToken ct)
    {
        List<IDomainEvent> appended;
        lock (_gate)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                stream = [];
                _streams[streamId] = stream;
            }

            // Current head version: -1 if empty, otherwise (count - 1).
            // This is the optimistic-concurrency check that gives us
            // single-stream serialisability without locking the whole store.
            var currentVersion = stream.Count - 1;
            if (currentVersion != expectedVersion)
                throw new ConcurrencyException(streamId, expectedVersion, currentVersion);

            stream.AddRange(events);
            appended = events.ToList();
        }

        // Subscriber dispatch happens OUTSIDE the lock. In a real store this
        // would be a separate thread tailing the log; for the demo we fan
        // out synchronously to keep ordering obvious.
        foreach (var e in appended)
            foreach (var sub in _subscribers)
                sub(streamId, e).GetAwaiter().GetResult();

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IDomainEvent>> Read(string streamId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
                return Task.FromResult<IReadOnlyList<IDomainEvent>>([]);

            // Copy so the caller never mutates the canonical list.
            return Task.FromResult<IReadOnlyList<IDomainEvent>>(stream.ToArray());
        }
    }

    public void Subscribe(Func<string, IDomainEvent, Task> onAppended)
        => _subscribers.Add(onAppended);
}
