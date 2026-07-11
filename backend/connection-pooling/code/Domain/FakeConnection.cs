namespace Domain;

// A stand-in for a real database connection (Npgsql, SqlClient, etc.).
// The *point* of connection pooling is that opening one of these is expensive
// (TCP handshake + TLS + auth + session init), and reusing it is cheap.
// We simulate that shape with a 100ms delay on Open and a 5ms delay on Execute.
public sealed class FakeConnection
{
    private static int _nextId;

    public int Id { get; }

    private FakeConnection(int id) => Id = id;

    // Simulates the expensive path: TCP + TLS + authentication + session defaults.
    // In real drivers this is 20-100 ms on a healthy LAN, more across regions.
    public static async Task<FakeConnection> Open(CancellationToken ct)
    {
        await Task.Delay(100, ct);
        return new FakeConnection(Interlocked.Increment(ref _nextId));
    }

    // Simulates running a small query on an already-open connection.
    // Nearly free compared to Open — that gap is what pooling exists to exploit.
    public Task Execute(CancellationToken ct) => Task.Delay(5, ct);
}
