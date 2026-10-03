using System.Threading.Channels;
using Domain.Prices;

namespace Application.Abstractions;

// Port to whatever produces events. In production this is a backplane with a durable,
// ordered log (Redis Streams, Kafka); here it is one in-memory adapter.
public interface IPriceFeed
{
    // Must be atomic: the backlog snapshot and the live channel meet exactly,
    // so a reconnecting client gets no gap and no duplicate between replay and live.
    FeedSubscription Subscribe();
}

// Dispose = unsubscribe. Forgetting this on client disconnect is the "zombie subscription" leak.
public sealed record FeedSubscription(
    IReadOnlyList<PriceTick> Backlog,
    ChannelReader<PriceTick> Live,
    Action Unsubscribe) : IDisposable
{
    public void Dispose() => Unsubscribe();
}
