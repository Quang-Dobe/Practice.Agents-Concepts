using Application.Abstractions;
using Application.Downstream;
using Application.Mediator;
using Domain;
using Infrastructure.Downstream;
using Infrastructure.Resilience;
using Microsoft.Extensions.DependencyInjection;

// ----- Composition root: wire everything up. -----
var services = new ServiceCollection();

// One breaker instance per logical dependency (practice doc, best practice #1).
// Tiny thresholds so we trip after ~3 failures — production values would be
// FailureRatio ~0.5, MinimumThroughput ~20+, BreakDuration ~15-60s.
var breaker = new CircuitBreaker(
    name:                   "downstream",
    windowSize:             5,
    minimumThroughput:      3,
    failureRatioThreshold:  0.5,
    breakDuration:          TimeSpan.FromMilliseconds(800),
    onTransition: (from, to, reason) =>
        Console.WriteLine($"  [breaker] {from} -> {to}  ({reason})"));

services.AddSingleton(breaker);

// The real downstream sits behind the breaker decorator. The Application layer
// asks for IDownstreamService and gets the wrapped one — it never sees the seam.
services.AddSingleton<FlakyDownstreamService>();
services.AddSingleton<IDownstreamService>(sp => new CircuitBrokenDownstreamService(
    inner:   sp.GetRequiredService<FlakyDownstreamService>(),
    breaker: sp.GetRequiredService<CircuitBreaker>()));

services.AddCustomMediator(typeof(CallDownstreamHandler).Assembly);

var provider = services.BuildServiceProvider();
var mediator = provider.GetRequiredService<IMediator>();

// ----- Driver: fire 25 calls and observe the breaker walk the state machine. -----
// Expect: a few Closed successes, a burst of failures that trips it Open, a
// stretch of fast SHORT-CIRCUITED rejections, then a HalfOpen probe that closes
// it again once the downstream has "recovered".
for (var i = 1; i <= 25; i++)
{
    var outcome = await mediator.Send(new CallDownstreamCommand());
    var status = outcome.Succeeded ? $"OK  {outcome.Payload}" : outcome.Error;
    Console.WriteLine($"call {i,2} [{breaker.State,-8}] {status}");

    // Pause longer than the breakDuration in the middle so we deterministically
    // cross the Open -> HalfOpen boundary while the downstream is still sick,
    // and again later once it's recovered.
    if (i == 12 || i == 18) await Task.Delay(1000);
}
