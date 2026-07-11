using System.Collections.Concurrent;
using System.Diagnostics;
using Application.Abstractions;
using Domain;

namespace Infrastructure;

// The topic itself, in miniature. Three moving parts, exactly as the deep-dive
// document describes them:
//   1. `_permits`   — the semaphore that caps concurrent lease holders at maxSize.
//                     This is the *ceiling*; every real pool has one.
//   2. `_idle`      — the free list of already-opened connections. Pop = fast path.
//                     If it's empty *and* we still hold a permit, we do the slow
//                     path (open a fresh connection).
//   3. `_waiters`   — a counter of tasks blocked on the semaphore. This is what
//                     shows up as "pending" in HikariCP and Npgsql metrics.
public sealed class SemaphorePool(int maxSize) : IConnectionPool
{
    private readonly SemaphoreSlim _permits = new(maxSize, maxSize);
    private readonly ConcurrentStack<FakeConnection> _idle = new();

    private int _opened;
    private int _reused;
    private int _waiters;
    private int _peakWaiters;
    private long _maxWaitTicks;

    public async Task<IConnectionLease> Acquire(TimeSpan timeout, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();

        // Bump waiter count *before* WaitAsync so the peak reflects "tasks trying
        // to get a permit right now". Snapshot metrics only mean anything if
        // you count the queue on entry, not on exit.
        var current = Interlocked.Increment(ref _waiters);
        InterlockedMax(ref _peakWaiters, current);

        var got = await _permits.WaitAsync(timeout, ct);
        Interlocked.Decrement(ref _waiters);

        // Fail fast on saturation. The "no timeout" default is the single most
        // common cause of "app is up but nothing works" — see practice doc, rule 3.
        if (!got) throw new TimeoutException($"Pool saturated: no lease within {timeout.TotalMilliseconds:F0} ms.");

        var waited = Stopwatch.GetElapsedTime(start);
        InterlockedMax64(ref _maxWaitTicks, waited.Ticks);

        // Fast path: pop an idle connection. This is the whole point of pooling —
        // skip the 100ms handshake and hand back a warm session.
        if (_idle.TryPop(out var reused))
        {
            Interlocked.Increment(ref _reused);
            return new PooledLease(reused, this);
        }

        // Slow path: the pool has a permit for us but no idle connection to reuse,
        // so open one. Happens N times total (N = maxSize) in steady state.
        var fresh = await FakeConnection.Open(ct);
        Interlocked.Increment(ref _opened);
        return new PooledLease(fresh, this);
    }

    // Called by PooledLease on dispose. Pushing before Release means a waiter
    // that wakes up will find something in _idle — no race window where a
    // permit is free but the free list is empty.
    internal void Return(FakeConnection conn)
    {
        _idle.Push(conn);
        _permits.Release();
    }

    public PoolStats Snapshot() =>
        new(_opened, _reused, _peakWaiters, new TimeSpan(Interlocked.Read(ref _maxWaitTicks)));

    public ValueTask DisposeAsync()
    {
        _permits.Dispose();
        return ValueTask.CompletedTask;
    }

    // Standard lock-free "atomic max" — CAS loop until the target holds our value
    // or a larger one. Interlocked has no built-in Max for int/long.
    private static void InterlockedMax(ref int target, int candidate)
    {
        int cur;
        do { cur = target; if (candidate <= cur) return; }
        while (Interlocked.CompareExchange(ref target, candidate, cur) != cur);
    }

    private static void InterlockedMax64(ref long target, long candidate)
    {
        long cur;
        do { cur = Interlocked.Read(ref target); if (candidate <= cur) return; }
        while (Interlocked.CompareExchange(ref target, candidate, cur) != cur);
    }
}

file sealed class PooledLease(FakeConnection conn, SemaphorePool pool) : IConnectionLease
{
    private int _returned;

    public int ConnectionId => conn.Id;

    public Task ExecuteWork(CancellationToken ct) => conn.Execute(ct);

    public ValueTask DisposeAsync()
    {
        // Guard against double-release: returning the same connection twice would
        // corrupt the idle stack (two callers, one physical connection) and
        // over-release the semaphore. Classic pool-implementation bug.
        if (Interlocked.Exchange(ref _returned, 1) == 0)
            pool.Return(conn);
        return ValueTask.CompletedTask;
    }
}
