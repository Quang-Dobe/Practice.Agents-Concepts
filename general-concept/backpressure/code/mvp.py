"""
Backpressure MVP: a fast producer + a slow consumer connected by a bounded
asyncio.Queue. Run with `python mvp.py` (3.11+).

The whole point: `await queue.put(item)` blocks the producer whenever the
buffer is full. That block IS the backpressure signal — no callbacks, no
`request(n)`, no protocol. The producer naturally paces itself to the
consumer's throughput because the runtime suspends it at the boundary.

Watch the timestamps: items are produced in tight bursts of QUEUE_CAPACITY,
then the producer stalls (visible as "queue full, producer waiting") until
the consumer drains a slot.
"""

import asyncio
import random
import time

QUEUE_CAPACITY = 3            # tight bound makes the stall obvious
ITEMS_TO_PRODUCE = 12
PRODUCER_MIN_DELAY = 0.05     # producer is ~10x faster than consumer
PRODUCER_MAX_DELAY = 0.15
CONSUMER_DELAY = 0.8          # slow, variable-cost sink (think DB write)

start = time.monotonic()


def elapsed() -> str:
    return f"t={time.monotonic() - start:5.2f}s"


async def producer(queue: asyncio.Queue[int]) -> None:
    for i in range(ITEMS_TO_PRODUCE):
        await asyncio.sleep(random.uniform(PRODUCER_MIN_DELAY, PRODUCER_MAX_DELAY))
        # The interesting moment: if queue is full, put() suspends here.
        # We log before/after so you can see the stall in the output.
        if queue.full():
            print(f"[{elapsed()}] producer: queue full, waiting to put item {i}")
        await queue.put(i)
        print(f"[{elapsed()}] producer: put item {i:2d} (queue size={queue.qsize()}/{QUEUE_CAPACITY})")
    # Sentinel: a None tells the consumer the stream is done.
    await queue.put(None)


async def consumer(queue: asyncio.Queue[int | None]) -> None:
    while True:
        item = await queue.get()
        if item is None:
            queue.task_done()
            return
        # Simulate work that is slower than the producer's emit rate.
        await asyncio.sleep(CONSUMER_DELAY)
        print(f"[{elapsed()}] consumer: done item {item:2d} (queue size={queue.qsize()}/{QUEUE_CAPACITY})")
        queue.task_done()


async def main() -> None:
    queue: asyncio.Queue[int | None] = asyncio.Queue(maxsize=QUEUE_CAPACITY)
    # Run them concurrently; the bounded queue is the only coordination.
    await asyncio.gather(producer(queue), consumer(queue))
    print(f"[{elapsed()}] done. producer was paced by consumer via the bounded queue.")


if __name__ == "__main__":
    random.seed(0)  # deterministic timing for reproducible demo output
    asyncio.run(main())
