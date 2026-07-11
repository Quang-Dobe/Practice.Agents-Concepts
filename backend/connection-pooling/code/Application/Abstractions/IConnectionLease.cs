namespace Application.Abstractions;

// A borrowed connection. DisposeAsync returns it to the pool; forgetting to
// dispose is the classic "connection leak" bug that drains a pool in seconds.
public interface IConnectionLease : IAsyncDisposable
{
    int ConnectionId { get; }

    // Simulates running work on the connection (a query, a transaction).
    // The identity of the connection matters — you can see reuse in the id.
    Task ExecuteWork(CancellationToken ct);
}
