namespace Domain.Prices;

// Where a reconnecting client left off, taken from the `Last-Event-ID` request header.
// The browser sends that header on its own after a drop; the server decides what it means.
public readonly record struct ResumeCursor(long? LastSeenId)
{
    // No header (or garbage) means a first-time client: no replay, start live.
    public static ResumeCursor Parse(string? lastEventId) =>
        new(long.TryParse(lastEventId, out var id) ? id : null);

    public bool IsFresh => LastSeenId is null;

    // Replay is only honest if the very next event the client needs is still in the log.
    // Otherwise we must say "reset" instead of silently skipping the gap.
    public bool CanResumeFrom(long oldestRetainedId) => LastSeenId + 1 >= oldestRetainedId;
}
