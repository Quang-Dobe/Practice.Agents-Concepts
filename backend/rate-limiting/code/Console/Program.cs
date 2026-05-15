using Application.Abstractions;
using Application.Mediator;
using Application.Requests;
using Infrastructure.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

// ----- Composition root: wire everything up. -----
var services = new ServiceCollection();

// Tiny, observable limits so the demo output is short and readable.
// Capacity 5, refill 2 tokens/sec → first 5 calls go through as a "burst",
// then we throttle to roughly 2/sec sustained. Production values would scope
// per (api_key, route_class) and run in the hundreds-to-thousands per minute.
services.AddSingleton<IRequestQuotaService>(
    new TokenBucketQuotaService(capacity: 5, refillPerSecond: 2));

services.AddCustomMediator(typeof(SubmitRequestHandler).Assembly);

var provider = services.BuildServiceProvider();
var mediator = provider.GetRequiredService<IMediator>();

// ----- Driver: fire 10 calls back-to-back, then wait, then fire 3 more. -----
// Expected timeline:
//   calls 1-5  : bucket starts full, all accepted (the "burst" property)
//   calls 6-10 : bucket empty, all 429 — only refill drips in between
//   pause 1s   : ~2 tokens drip back in at 2 tokens/sec
//   calls 11-13: first two accepted from the refill, third 429 again
//
// Same callerKey for every call — keying by identity, not IP, is best
// practice #1 from the practice doc. Swap "user-42" for an IP and the demo
// turns into "the corporate-NAT punishment" anti-pattern.
const string callerKey = "user-42";

async Task FireBurst(int count)
{
    for (var i = 1; i <= count; i++)
    {
        var outcome = await mediator.Send(new SubmitRequestCommand(callerKey));
        var verdict = outcome.Accepted ? "ALLOWED" : "REJECTED";
        var detail = outcome.Accepted
            ? $"tokens left: {outcome.TokensRemaining:F2}"
            : outcome.Error;
        Console.WriteLine($"call {i,2} [{verdict,-8}] {detail}");
    }
}

Console.WriteLine("--- burst of 10 (capacity is 5) ---");
await FireBurst(10);

Console.WriteLine("--- sleep 1s, ~2 tokens drip back in ---");
await Task.Delay(1000);

Console.WriteLine("--- burst of 3 ---");
await FireBurst(3);
