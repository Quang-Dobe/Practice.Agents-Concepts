using System.Threading.Channels;
using Application.Abstractions;
using Domain.Prices;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Feeds;

// Publisher + replay log + subscriber list in one process. This is the "static subscriber list"
// from 03-practice.md: correct on one instance, loses events on two. Swap in a backplane to scale.
public sealed class InMemoryPriceFeed : BackgroundService, IPriceFeed
{
    private const int Retention = 5; // tiny on purpose, so the demo can age a cursor out
    private readonly Queue<PriceTick> _log = new();
    private readonly List<Channel<PriceTick>> _subscribers = [];
    private readonly Lock _gate = new();

    public FeedSubscription Subscribe()
    {
        // Bounded + DropOldest: a slow client loses stale ticks instead of growing server memory
        // or stalling the publisher for everyone else.
        var channel = Channel.CreateBounded<PriceTick>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

        // Same lock as Publish: no tick can land between the snapshot and the subscription.
        lock (_gate)
        {
            _subscribers.Add(channel);
            return new FeedSubscription([.. _log], channel.Reader,
                () => { lock (_gate) _subscribers.Remove(channel); });
        }
    }

    // One tick every 200 ms, whether or not anyone is listening.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = PriceTick.First("ACME");
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            lock (_gate)
            {
                _log.Enqueue(tick);
                if (_log.Count > Retention) _log.Dequeue();
                foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(tick);
            }
            tick = tick.Next();
        }
    }
}
