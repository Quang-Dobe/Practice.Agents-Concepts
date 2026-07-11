using Application.Abstractions;
using Domain;

namespace Infrastructure;

// The baseline: no pooling. Every Acquire opens a fresh FakeConnection; every
// DisposeAsync throws it away. This is what a naive `new NpgsqlConnection()`
// per request would look like without the driver's built-in pool.
public sealed class NoPool : IConnectionPool
{
    private int _opened;

    public async Task<IConnectionLease> Acquire(TimeSpan _, CancellationToken ct)
    {
        // The expensive path — every single time. Handshake cost is never amortized.
        var conn = await FakeConnection.Open(ct);
        Interlocked.Increment(ref _opened);
        return new NoPoolLease(conn);
    }

    // No reuse, no waiters, no queue — everything is zero except opens.
    public PoolStats Snapshot() => new(_opened, 0, 0, TimeSpan.Zero);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// `file` scope keeps this adapter's helper invisible outside NoPool.cs — one
// public type per file, but private helpers live where they belong.
file sealed class NoPoolLease(FakeConnection conn) : IConnectionLease
{
    public int ConnectionId => conn.Id;
    public Task ExecuteWork(CancellationToken ct) => conn.Execute(ct);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // "closing" is a no-op in the simulation
}
