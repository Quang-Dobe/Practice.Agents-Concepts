using Application.Abstractions;
using Application.Mediator;
using Application.Users;
using Infrastructure.Persistence;
using Infrastructure.Sharding;
using Microsoft.Extensions.DependencyInjection;

// ----------------------------------------------------------------------------
// What this demo proves:
//   1. A router maps shard_key -> shard_id, and a handler never sees more
//      than that single shard.
//   2. Hash-modulo distributes evenly but is brittle under N changes.
//   3. Consistent hashing distributes nearly as evenly and only moves K/N
//      keys when shards are added — the property that makes Cassandra/Dynamo
//      style rings practical.
//   4. The Application layer (commands/queries via the mediator) is identical
//      regardless of which router is plugged in — sharding is an Infrastructure
//      concern.
// ----------------------------------------------------------------------------

const int ShardCount = 4;
const int UserCount = 12;

// Deterministic Guids so successive runs produce the same routing decisions.
// Makes the "look which shard each one landed on" observation reproducible.
var users = Enumerable.Range(1, UserCount)
    .Select(i => (Id: DeterministicGuid(i), Name: $"user-{i:D2}"))
    .ToArray();

await RunDemo("Hash modulo", new HashModuloRouter(ShardCount));
await RunDemo("Consistent hash", new ConsistentHashRouter(ShardCount));

// Bonus: the rebalancing experiment. Same keys, but now grow from 4 -> 5
// shards. We print how many keys *moved* under each strategy. This is the
// single most important number when comparing the two routers.
RebalanceComparison(users);
return;

async Task RunDemo(string label, IShardRouter router)
{
    // Each run gets its own DI container so handlers resolve against the
    // router we're testing right now.
    var services = new ServiceCollection();
    services.AddCustomMediator(typeof(AddUserCommand).Assembly);
    services.AddSingleton(router);
    services.AddSingleton<ShardedUserRepository>(
        sp => new ShardedUserRepository(sp.GetRequiredService<IShardRouter>(), ShardCount));
    services.AddSingleton<IUserRepository>(sp => sp.GetRequiredService<ShardedUserRepository>());

    using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
    var repo = scope.ServiceProvider.GetRequiredService<ShardedUserRepository>();

    Console.WriteLine($"\n=== {label} ({router.StrategyName}) ===");

    // Inserts go through the mediator -> handler -> repo -> router path.
    // The handler is unchanged across strategies; only the router differs.
    foreach (var u in users)
    {
        var shard = await mediator.Send(new AddUserCommand(u.Id, u.Name));
        Console.WriteLine($"  INSERT {u.Name}  id={Short(u.Id)}  -> shard {shard}");
    }

    // Read-back: every query carries the shard key (Id) in its predicate,
    // so each lookup hits exactly one shard. Compare to a scatter-gather
    // search by name, which would have to fan out to all N shards.
    var probeUser = users[3];
    var result = await mediator.Send(new GetUserQuery(probeUser.Id));
    Console.WriteLine($"  SELECT id={Short(probeUser.Id)}  -> shard {result.ShardId}, " +
                      $"found='{result.User?.Name}'");

    // Per-shard counts — the dashboard metric 03-practice.md insists you
    // alert on. Visible skew here would be your first hot-shard warning.
    var counts = Enumerable.Range(0, ShardCount).Select(repo.CountOnShard);
    Console.WriteLine($"  distribution: [{string.Join(", ", counts)}]");
}

void RebalanceComparison((Guid Id, string Name)[] keys)
{
    Console.WriteLine("\n=== Rebalance: grow from 4 -> 5 shards ===");

    int Moved(IShardRouter before, IShardRouter after) =>
        keys.Count(k => before.RouteToShard(k.Id) != after.RouteToShard(k.Id));

    var modBefore = new HashModuloRouter(4);
    var modAfter = new HashModuloRouter(5);
    var conBefore = new ConsistentHashRouter(4);
    var conAfter = new ConsistentHashRouter(5);

    // With N=4 -> 5, plain modulo will rehash roughly (N-1)/N = ~80% of keys.
    // Consistent hashing should move only ~K/N ≈ 20%. The exact numbers depend
    // on the sample, but the ratio is the lesson.
    Console.WriteLine($"  hash-modulo moved      {Moved(modBefore, modAfter)} / {keys.Length} keys");
    Console.WriteLine($"  consistent-hash moved  {Moved(conBefore, conAfter)} / {keys.Length} keys");
}

// Helpers
static string Short(Guid g) => g.ToString()[..8];

static Guid DeterministicGuid(int seed)
{
    // Build a Guid from a fixed seed so output is reproducible across runs.
    var bytes = new byte[16];
    BitConverter.GetBytes(seed).CopyTo(bytes, 0);
    bytes[8] = 0x42; // any constant so we don't look like the empty Guid
    return new Guid(bytes);
}
