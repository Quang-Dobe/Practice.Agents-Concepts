// Server-Sent Events end to end: one GET, a response that never finishes, and resume via Last-Event-ID.
//
// The server half is the /prices endpoint. The client half is the driver at the bottom, which reads
// the raw stream with HttpClient so you see the exact bytes a browser's EventSource would parse.
// It proves three things: the wire format, gap-free resume after a drop, and "reset" when the
// client's cursor is older than the server's replay log.

using System.Net.ServerSentEvents;
using Application.Abstractions;
using Application.Mediator;
using Application.Prices;
using Infrastructure.Feeds;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // keep stdout to the demo trace
builder.Services.AddCustomMediator(typeof(StreamPricesQuery).Assembly);
// One instance plays two roles: the port handlers subscribe to, and the hosted publisher loop.
builder.Services.AddSingleton<InMemoryPriceFeed>();
builder.Services.AddSingleton<IPriceFeed>(sp => sp.GetRequiredService<InMemoryPriceFeed>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<InMemoryPriceFeed>());

var app = builder.Build();
app.Urls.Add("http://localhost:5199");

// The browser sends Last-Event-ID by itself on reconnect. Cancel on client disconnect AND on shutdown,
// or every open stream holds the process for the whole shutdown timeout on each deploy.
app.MapGet("/prices", async ([FromHeader(Name = "Last-Event-ID")] string? lastEventId,
    IMediator mediator, IHostApplicationLifetime lifetime, HttpContext http) =>
{
    var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
    http.Response.RegisterForDispose(cts);
    // .NET 10: sets Content-Type: text/event-stream, writes the fields, and flushes after every item.
    return TypedResults.ServerSentEvents(ToSse(await mediator.Send(new StreamPricesQuery(lastEventId), cts.Token)));
});

await app.StartAsync();
await Task.Delay(1200); // let the publisher fill its 5-tick replay log

using var client = new HttpClient { BaseAddress = new Uri("http://localhost:5199") };

Console.WriteLine("== 1. Fresh connect, no Last-Event-ID: raw text/event-stream ==");
var lastId = await Listen(client, lastEventId: null, events: 3, raw: true);

Console.WriteLine($"== Dropped after id {lastId}. Publisher keeps going for 600 ms ==\n");
await Task.Delay(600);

Console.WriteLine($"== 2. Reconnect with Last-Event-ID: {lastId} -> missed ticks replayed, no gap ==");
await Listen(client, lastId, events: 5, raw: false);

Console.WriteLine("\n== 3. Reconnect with Last-Event-ID: 1 -> older than the replay log ==");
await Listen(client, "1", events: 2, raw: false);

Console.WriteLine("\nServer still up. Try: curl -N http://localhost:5199/prices   (Ctrl+C to stop)");
await app.WaitForShutdownAsync();

// Edge mapping: application events become SSE fields. Ids are the log sequence, so they are real cursors.
static async IAsyncEnumerable<SseItem<object>> ToSse(IAsyncEnumerable<FeedEvent> events)
{
    var first = true;
    await foreach (var e in events)
    {
        yield return new SseItem<object>(e.Data, e.Type)
        {
            EventId = e.Id?.ToString(),
            // `retry:` once per connection is enough; the client remembers it.
            ReconnectionInterval = first ? TimeSpan.FromSeconds(1) : null,
        };
        first = false;
    }
}

// A tiny hand-written client: read lines, a blank line ends one event, remember the last `id:`.
static async Task<string?> Listen(HttpClient client, string? lastEventId, int events, bool raw)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/prices");
    if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);

    // ResponseHeadersRead: without it HttpClient waits for the body to end, which never happens.
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (raw) Console.WriteLine($"Content-Type: {response.Content.Headers.ContentType}\n");

    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
    var fields = new List<string>();
    for (var seen = 0; seen < events && await reader.ReadLineAsync() is { } line;)
    {
        if (line.StartsWith("id: ")) lastEventId = line[4..];
        if (line.Length > 0) { fields.Add(line); continue; }
        Console.WriteLine(raw ? string.Join('\n', fields) + "\n" : "  " + string.Join("  |  ", fields));
        fields.Clear();
        seen++;
    }
    return lastEventId; // disposing the response here is the "connection drop"
}
