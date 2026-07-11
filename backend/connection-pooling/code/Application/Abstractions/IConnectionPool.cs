namespace Application.Abstractions;

// The port. Application code only knows this shape — the infrastructure layer
// swaps in different adapters (no-pool vs bounded pool) without the handler noticing.
// This is the Clean Arch seam that makes the demo possible: same use case,
// two adapters, visibly different behavior.
public interface IConnectionPool : IAsyncDisposable
{
    // Reserves a connection for exclusive use. The returned lease MUST be disposed
    // to return the connection — that's the whole pooling contract. If the pool
    // is saturated and nothing frees up within `timeout`, throws TimeoutException.
    Task<IConnectionLease> Acquire(TimeSpan timeout, CancellationToken ct);

    // Point-in-time metrics. In real drivers these are the Micrometer / Npgsql
    // counters ops teams alert on: active, idle, waiters, acquire-time histogram.
    PoolStats Snapshot();
}
