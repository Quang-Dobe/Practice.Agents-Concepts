# Rate Limiting — MVP Code

The smallest runnable demo of a token-bucket rate limiter. ~120 lines of code across four projects.

## What it demonstrates
- The **token bucket** algorithm — capacity + refill rate, lazy refill on each call — exactly the math from step 2-4 of `02-deep-dive.md`.
- The Clean Architecture seam: `IRequestQuotaService` lives in `Application`, the bucket lives in `Infrastructure`; the handler never learns the algorithm.
- Burst tolerance vs. sustained throttling — first 5 calls go through, the next 5 are rejected, a pause lets tokens drip back in.
- The shape of a `429` response — `RetryAfter` and `TokensRemaining` carry the info an HTTP layer would emit as `Retry-After` and `RateLimit-Remaining` headers.

## Prerequisites
**.NET 8.0 SDK** (or newer). Verify with `dotnet --version`. Everything runs in-process — no Redis, no HTTP server.

## Run it
```bash
cd code && dotnet run --project Console
```

## Expected output
```
--- burst of 10 (capacity is 5) ---
call  1 [ALLOWED ] tokens left: 4.00
call  2 [ALLOWED ] tokens left: 3.00
call  3 [ALLOWED ] tokens left: 2.00
call  4 [ALLOWED ] tokens left: 1.00
call  5 [ALLOWED ] tokens left: 0.00
call  6 [REJECTED] 429 Too Many Requests (retry after 500 ms)
call  7 [REJECTED] 429 Too Many Requests (retry after 500 ms)
call  8 [REJECTED] 429 Too Many Requests (retry after 500 ms)
call  9 [REJECTED] 429 Too Many Requests (retry after 500 ms)
call 10 [REJECTED] 429 Too Many Requests (retry after 500 ms)
--- sleep 1s, ~2 tokens drip back in ---
--- burst of 3 ---
call  1 [ALLOWED ] tokens left: 1.00
call  2 [ALLOWED ] tokens left: 0.00
call  3 [REJECTED] 429 Too Many Requests (retry after 500 ms)
```

## What to try next
- Change `capacity: 5` to `1` in `Program.cs` — only one call/burst, the rest queue on the refill rate.
- Raise `refillPerSecond` to `20` — the second burst is fully accepted and rejection becomes hard to provoke.
- Swap the second burst's `callerKey` to `"user-99"` and watch a fresh bucket start full (per-key isolation).
- Comment out the `await Task.Delay(1000)` and see the second burst rejected immediately — no time has passed, no tokens refilled.
