# Circuit Breaker — MVP Code

The smallest runnable demo of a hand-rolled circuit breaker. ~130 lines of code across four projects.

## What it demonstrates
- The three-state machine — **Closed**, **Open**, **HalfOpen** — with the transitions from `02-deep-dive.md`.
- A count-based sliding window with failure-ratio threshold + minimum-throughput floor — one bad call cannot trip it.
- A single-permit Half-Open probe: only one caller tests recovery; the rest are rejected as if still Open.
- The Clean Architecture seam — the breaker is an `Infrastructure` decorator around the `IDownstreamService` port.

## Prerequisites
**.NET 8.0 SDK** (or newer). Verify with `dotnet --version`. Everything runs in-process — no external services.

## Run it
```bash
cd code && dotnet run --project Console
```

## Expected output
The downstream is healthy for 3 warm-up calls, sick for 1.5s, then recovers. You will see roughly:
```
call  1 [Closed  ] OK  ok (warm-up #1)
call  4 [Closed  ] FAILED: downstream 503
  [breaker] Closed -> Open  (failure ratio 60% >= 50% over 5 samples)
call  7 [Open    ] SHORT-CIRCUITED: Circuit 'downstream' is open ...
  [breaker] Open -> HalfOpen  (break duration elapsed)
  [breaker] HalfOpen -> Open  (probe failed)
  [breaker] Open -> HalfOpen  (break duration elapsed)
  [breaker] HalfOpen -> Closed  (probe succeeded)
call 19 [Closed  ] OK  ok (recovered)
```

## What to try next
- Raise `failureRatioThreshold` to `0.8` in `Program.cs` and watch the breaker tolerate more failures before tripping.
- Drop `breakDuration` to `200ms` and see the breaker probe so often the downstream never gets room to recover.
- Comment out the `Task.Delay(1000)` after call 18 — the demo ends with the breaker stuck Open (the case for Open-duration alerts).
