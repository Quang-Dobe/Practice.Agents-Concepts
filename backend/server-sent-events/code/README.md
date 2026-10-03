# Server-Sent Events — MVP Code

The smallest runnable demo of an SSE stream with resume. About 220 lines of code across four projects, about 55 of them the shared mediator.

## What it demonstrates
- **Wire format**: `TypedResults.ServerSentEvents` (.NET 10) writes `event:`, `data:`, `id:`, `retry:` and flushes per event. The demo prints the raw bytes (see `../docs/02-deep-dive.md § How`).
- **Resume with `Last-Event-ID`**: ids are log sequence numbers, so a reconnect replays exactly the missed ticks. Subscribing and taking the backlog snapshot under one lock rules out gaps and duplicates (`Infrastructure/Feeds/InMemoryPriceFeed.cs`).
- **The "too old" case**: a cursor that has aged out of the 5-tick log gets a `reset` event, not a silent skip (`Domain/Prices/ResumeCursor.cs`).
- **Disconnect hygiene**: the stream is cancelled by `RequestAborted` + `ApplicationStopping`, and `using` unsubscribes. Each client gets a bounded `DropOldest` queue.

## Prerequisites
- .NET SDK **10.0+**. `SseItem<T>` and `TypedResults.ServerSentEvents` don't exist in 8 or 9.
- No external services. Binds `http://localhost:5199`.

## Run it
```bash
dotnet run --project Api
```
In a second terminal while it runs: `curl -N http://localhost:5199/prices` (`-N` turns off curl's buffering).

## Expected output
Ids vary with timing, but the shape doesn't:
```
event: tick
data: {"id":7,"symbol":"ACME","price":101.50}
id: 7
retry: 1000
...
== 2. Reconnect with Last-Event-ID: 9 -> missed ticks replayed, no gap ==
  event: tick  |  data: {"id":10,...}  |  id: 10  |  retry: 1000
== 3. Reconnect with Last-Event-ID: 1 -> older than the replay log ==
  event: reset  |  data: {"reason":"Last-Event-ID is older than the replay log; refetch a snapshot"}  |  retry: 1000
```

## What to try next
- Change `Task.Delay(600)` in `Program.cs` to `2000`. Phase 2 now gets `reset`: the gap outgrew the 5-tick log.
- Delete the `CanResumeFrom` check in `StreamPricesQuery.cs` so the backlog always replays. Phase 3 then looks like a clean resume while ids 2..9 are lost: the "IDs without memory" anti-pattern.
- Set `Retention` in `InMemoryPriceFeed.cs` to `50`. Phase 3 now replays from id 2 instead of resetting.
- Restart the app, then send `curl -N -H "Last-Event-ID: 9999" ...`. A cursor from a previous process is treated as current because ids restart at 1, which is why production needs a durable shared log.
