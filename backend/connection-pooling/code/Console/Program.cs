using Application.Abstractions;
using Application.Mediator;
using Application.Workload;
using Infrastructure;
using Microsoft.Extensions.DependencyInjection;

// Composition root. We build one DI container per pool implementation, run the
// same RunWorkloadCommand through the mediator, and print the pool stats.
// The point: the workload handler is identical for both runs — the only thing
// that changes is which IConnectionPool adapter is registered.
//
// Numbers picked so the shape is obvious:
//   30 ops, 10 concurrent, pool max 3 -> ~7 waiters at peak, 3 opens vs 30.

await RunAndPrint("No pool: fresh connection every call",  new NoPool());
await RunAndPrint("Pool (max 3): bounded reuse + waiters", new SemaphorePool(maxSize: 3));

static async Task RunAndPrint(string label, IConnectionPool pool)
{
    var services = new ServiceCollection()
        .AddSingleton(pool) // singleton: one pool for the process, per Clean Arch composition root
        .AddCustomMediator(typeof(RunWorkloadCommand).Assembly);

    await using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();
    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

    var report = await mediator.Send(
        new RunWorkloadCommand(30, 10, TimeSpan.FromSeconds(2)));

    Console.WriteLine($"=== {label} ===");
    Console.WriteLine($"  ran {report.Operations} ops in {report.Elapsed.TotalMilliseconds:F0} ms");
    Console.WriteLine($"  connections opened : {report.Stats.TotalOpened}");
    Console.WriteLine($"  connections reused : {report.Stats.TotalReused}");
    if (report.Stats.PeakWaiters > 0)
        Console.WriteLine($"  peak waiters       : {report.Stats.PeakWaiters}");
    if (report.Stats.MaxWait > TimeSpan.Zero)
        Console.WriteLine($"  max lease wait     : {report.Stats.MaxWait.TotalMilliseconds:F0} ms");
    Console.WriteLine();
}
