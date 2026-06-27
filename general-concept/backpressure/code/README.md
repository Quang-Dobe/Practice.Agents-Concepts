# Backpressure — MVP Code

The smallest runnable demo of backpressure: a fast producer and a slow consumer connected by a bounded `asyncio.Queue`. About 30 lines of actual code, comments excluded.

## What it demonstrates

- A bounded buffer between an async producer and consumer is the simplest backpressure mechanism — no protocol, no `request(n)`, no callbacks.
- `await queue.put(item)` *blocks* when the buffer is full. That suspension IS the backpressure signal.
- After an initial burst that fills the queue, the producer naturally paces itself to the consumer's throughput; the system stays bounded in memory.
- This is the "block" strategy from the four options in `02-deep-dive.md` (buffer, drop, block, signal). The first three lines of the deep-dive's tradeoff table show up live in the output.

## Prerequisites

- Python 3.11+ (uses PEP-604 `int | None` syntax).
- No third-party dependencies. Pure stdlib: `asyncio`, `random`, `time`.

## Run it

```bash
python3 mvp.py
```

## Expected output

You should see:

1. An initial burst where the producer fills the queue (sizes 1/3, 1/3, 2/3, 3/3) within the first ~0.4s — that's the buffer absorbing the burst.
2. From there on, lines alternate: `consumer: done item N` immediately followed by `producer: put item N+3`. The producer's emit timestamps now match the consumer's drain timestamps — it has been paced.
3. Repeated `queue full, waiting to put item X` lines confirm the producer is suspended at the bounded-queue boundary, not running ahead and allocating memory.
4. Total runtime ~9–10 seconds for 12 items at 0.8s each — consumer-bound, as expected.

## What to try next

- Change `QUEUE_CAPACITY` to `100` and notice the producer finishes early and the queue acts as an unbounded buffer (bufferbloat in miniature).
- Swap `await queue.put(i)` for `queue.put_nowait(i)` wrapped in a `try/except asyncio.QueueFull` that prints "DROPPED" — that's the "drop" strategy from the deep dive.
- Set `CONSUMER_DELAY = 0.01` (faster than the producer) and watch the queue stay near-empty — backpressure costs nothing when the consumer keeps up.
- Remove `maxsize=QUEUE_CAPACITY` from the `asyncio.Queue(...)` call and observe the producer finishing in ~1.5s while the consumer is still processing item 1 — that is an unbounded queue, a.k.a. an OOM with a polite name.
