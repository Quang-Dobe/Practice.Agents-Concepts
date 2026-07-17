using Application.Abstractions;
using Application.Mediator;
using Application.Store;
using Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

// Demo flow (see ../docs/02-deep-dive.md § How for the concepts):
//
//   1. Open (or create) wal.log next to the executable.
//   2. RUN THE REPLAY FIRST. If the log has entries from a previous run
//      (including one that crashed), the store is rebuilt from them before
//      any new writes are accepted. This is recovery.
//   3. Perform some Put/Delete commands. Each one is durably logged BEFORE
//      the in-memory dictionary is mutated - that is the WAL invariant.
//   4. On the second run (pass --crash to the first), the process exits
//      mid-way. The next `dotnet run` will replay the log and show that
//      every committed write survived the kill.
//
// Everything else is DI plumbing.

var walPath = Path.Combine(AppContext.BaseDirectory, "wal.log");
var crashAfter = args.Contains("--crash") ? 2 : int.MaxValue;

// -------- Composition root --------
var services = new ServiceCollection();

// One long-lived WAL adapter and one in-memory store. Both are singletons
// because they hold the file handle and the map respectively.
services.AddSingleton<IWriteAheadLog>(_ => new FileWriteAheadLog(walPath));
services.AddSingleton<KeyValueStore>();

// Register every IRequestHandler<,> in the Application assembly. The custom
// mediator resolves them by convention - no per-handler wiring.
services.AddCustomMediator(typeof(PutCommand).Assembly);

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
var store = scope.ServiceProvider.GetRequiredService<KeyValueStore>();

// -------- Step 1: recover from the log before accepting any writes --------
var replayed = await mediator.Send(new RecoverCommand());
Console.WriteLine($"Recovered {replayed} record(s) from {walPath}");
Console.WriteLine($"State after recovery: {Format(store.Snapshot())}");

// -------- Step 2: perform some new writes --------
// Each Put/Delete goes through the mediator -> handler -> WAL.Append(fsync)
// -> in-memory update. If the process dies between two of these ops, the
// log is the source of truth on the next run.
var ops = new (string kind, string key, string? value)[]
{
    ("PUT", "user:1", "alice"),
    ("PUT", "user:2", "bob"),
    ("PUT", "user:1", "alice-v2"),   // overwrite; replay must honour order
    ("DEL", "user:2", null),
    ("PUT", "user:3", "carol"),
};

for (var i = 0; i < ops.Length; i++)
{
    var (kind, key, value) = ops[i];

    if (kind == "PUT")
        await mediator.Send(new PutCommand(key, value!));
    else
        await mediator.Send(new DeleteCommand(key));

    Console.WriteLine($"  applied op {i + 1}: {kind} {key} {value}");

    // Simulate a crash BEFORE the remaining ops are logged. The ones we
    // already appended are safe on disk; the ones we skipped are not
    // acknowledged and simply never happened as far as the user is concerned.
    if (i + 1 == crashAfter)
    {
        Console.WriteLine("*** simulated crash: exiting without further writes ***");
        Environment.Exit(1);
    }
}

Console.WriteLine($"Final state: {Format(store.Snapshot())}");

static string Format(IReadOnlyDictionary<string, string> map) =>
    map.Count == 0 ? "(empty)" : "{ " + string.Join(", ", map.Select(kv => $"{kv.Key}={kv.Value}")) + " }";
