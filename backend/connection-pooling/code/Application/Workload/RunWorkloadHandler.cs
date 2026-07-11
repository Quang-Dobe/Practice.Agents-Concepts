using System.Diagnostics;
using Application.Abstractions;
using Application.Mediator;

namespace Application.Workload;

// The handler is deliberately dumb: it doesn't know or care whether the pool
// reuses connections. That's the point — the pool is a policy, injected through
// the port, and the same use case works against every adapter.
public sealed class RunWorkloadHandler(IConnectionPool pool)
    : IRequestHandler<RunWorkloadCommand, WorkloadReport>
{
    public async Task<WorkloadReport> Handle(RunWorkloadCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Cap the caller-side concurrency so the pool sees pressure but is not
        // handed 1000 tasks at once (that would just measure the ThreadPool).
        using var throttle = new SemaphoreSlim(cmd.Concurrency);

        var tasks = Enumerable.Range(0, cmd.Operations).Select(async _ =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                // The whole pooling contract in three lines: acquire, work, dispose.
                // The `await using` is what returns the lease — miss it and the pool leaks.
                await using var lease = await pool.Acquire(cmd.AcquireTimeout, ct);
                await lease.ExecuteWork(ct);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        sw.Stop();
        return new WorkloadReport(cmd.Operations, sw.Elapsed, pool.Snapshot());
    }
}
