using Domain.Prices;

namespace Application.Prices;

// Transport-neutral event. The Api layer maps it 1:1 onto SSE fields:
// Type -> `event:`, Id -> `id:`, Data -> JSON in `data:`.
public sealed record FeedEvent(string Type, long? Id, object Data)
{
    public static FeedEvent Of(PriceTick tick) => new("tick", tick.Id, tick);

    // No id on purpose: the client keeps its old cursor until the next real tick replaces it.
    public static FeedEvent Reset { get; } = new("reset", null,
        new { reason = "Last-Event-ID is older than the replay log; refetch a snapshot" });
}
