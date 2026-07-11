# Connection Pooling — MVP Code

Smallest runnable demo of a bounded connection pool with a wait queue and acquire timeout, versus a naive "open every call" baseline. Same workload, two adapters, visibly different behavior.

## What it demonstrates

- **Amortized handshake cost** — the pool opens 3 connections and reuses them 27 times; the baseline opens all 30.
- **Bounded concurrency** — the pool's semaphore is the ceiling. Peak waiters is what saturation looks like from inside the app.
- **Fail-fast acquire timeout** — `Acquire(timeout, ct)` throws `TimeoutException` if no lease is available in time.
- **Clean Arch port/adapter seam** — `IConnectionPool` is the port; `NoPool` and `SemaphorePool` are swappable adapters. The mediator handler doesn't know which is wired.

## Prerequisites

- .NET SDK 8.0 or newer. No external services — the "connection" is a simulated 100 ms handshake in `Domain/FakeConnection.cs`.

## Run it

```bash
dotnet run --project Console
```

## Expected output (approximate — ratios hold)

```
=== No pool: fresh connection every call ===
  ran 30 ops in ~320 ms
  connections opened : 30 ; reused : 0

=== Pool (max 3): bounded reuse + waiters ===
  ran 30 ops in ~160 ms
  connections opened : 3  ; reused : 27
  peak waiters       : 7  ; max lease wait : ~100 ms
```

## What to try next

- Drop the `TimeSpan.FromSeconds(2)` timeout to `20` ms in `Program.cs` — watch `SemaphorePool` throw.
- Raise `maxSize` from 3 to 10 — opened count climbs; bigger pools trade handshakes for wait time.
- Add `await Task.Delay(500)` inside one-in-five `ExecuteWork` calls — that's the "slow query drains the pool" shape.
