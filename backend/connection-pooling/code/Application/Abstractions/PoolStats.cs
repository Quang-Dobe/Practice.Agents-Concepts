namespace Application.Abstractions;

// The numbers that make pooling visible. TotalOpened vs TotalReused is the
// headline metric: it's the ratio you're actually paying for. PeakWaiters and
// MaxWait are what saturation looks like — when they climb, you're either
// undersized or holding leases too long.
public sealed record PoolStats(
    int TotalOpened,
    int TotalReused,
    int PeakWaiters,
    TimeSpan MaxWait);
