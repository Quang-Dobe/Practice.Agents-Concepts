using System.Runtime.CompilerServices;
using Application.Abstractions;
using Application.Mediator;
using Domain.Prices;

namespace Application.Prices;

// The query result is an endless async stream, not a value. The mediator stays unchanged:
// it hands back the IAsyncEnumerable, and the endpoint pulls from it for as long as the client listens.
public sealed record StreamPricesQuery(string? LastEventId) : IRequest<IAsyncEnumerable<FeedEvent>>;

public sealed class StreamPricesHandler(IPriceFeed feed)
    : IRequestHandler<StreamPricesQuery, IAsyncEnumerable<FeedEvent>>
{
    public Task<IAsyncEnumerable<FeedEvent>> Handle(StreamPricesQuery query, CancellationToken ct) =>
        Task.FromResult(Stream(ResumeCursor.Parse(query.LastEventId), ct));

    private async IAsyncEnumerable<FeedEvent> Stream(
        ResumeCursor cursor, [EnumeratorCancellation] CancellationToken ct)
    {
        // `using` unsubscribes when the client disconnects: ct fires, the loop below throws,
        // and the iterator's finally block runs Dispose.
        using var sub = feed.Subscribe();

        // Resume path: replay only what the client missed, or admit we can't.
        if (!cursor.IsFresh && sub.Backlog.Count > 0)
        {
            if (!cursor.CanResumeFrom(sub.Backlog[0].Id))
                yield return FeedEvent.Reset;
            else
                foreach (var missed in sub.Backlog.Where(t => t.Id > cursor.LastSeenId))
                    yield return FeedEvent.Of(missed);
        }

        // Live path: one event per publish, until the client leaves or the server stops.
        await foreach (var tick in sub.Live.ReadAllAsync(ct))
            yield return FeedEvent.Of(tick);
    }
}
