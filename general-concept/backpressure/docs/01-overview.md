# Backpressure — Overview

> Backpressure is the mechanism by which a slow consumer tells a fast producer to ease up, so the system bends instead of breaking.

## The 30-second version
In any pipeline — network sockets, message queues, streams, UI event loops — one side produces data and another side consumes it. They almost never run at the same speed. Backpressure is the family of techniques that handle the mismatch on purpose, instead of letting memory balloon, latency spike, or the whole process crash. If you have ever watched a Kafka consumer fall hours behind, or seen a Node.js stream eat all your RAM, you have met the absence of backpressure.

## The mental model
Picture a busy restaurant kitchen on a Friday night. Waiters (producers) keep firing tickets at the line cook (consumer). The cook can only plate so many dishes a minute. What happens when tickets arrive faster than plates leave?

There are exactly four things the kitchen can do, and they are exactly the four things every backpressure-aware system can do:

1. **Buffer** — clip the tickets to a rail. Works until the rail is full. Then what?
2. **Drop** — toss the new tickets in the bin. The customer never gets food, but the kitchen survives.
3. **Block** — tell the waiters "stop taking orders until I catch up." The dining room queues at the door.
4. **Signal slowdown** — wave the waiters off: "bring me three tickets at a time, not thirty." The pace adjusts at the source.

That fourth option is the heart of *reactive* backpressure: the consumer publishes its appetite, and the producer respects it. The first three are what happens when nobody designed for the mismatch — they emerge whether you wanted them or not, and usually in the worst form (silent drops, OOM kills, mysterious latency).

## What it is NOT
- Not **rate limiting**. Rate limiting caps a producer from the outside ("max 100 req/sec"); backpressure is the consumer's voice from the inside ("I can take 7 more right now").
- Not **load shedding**. Load shedding is one specific response (drop) under stress; backpressure is the broader conversation that decides whether to shed.
- Not **a queue**. A queue is the buffer in option 1. Backpressure is what you do *when the queue fills*.
- Not **retry logic**. Retries push work back into the system; backpressure tries to stop pushing in the first place.

## When you would reach for it
- Streaming data between services where the consumer's speed varies (DB writers, downstream APIs, batch ML inference).
- Reading large files or network responses chunk-by-chunk on a constrained worker.
- Building a message-driven system where one slow subscriber must not poison the whole topic.
- Anywhere a producer is "fire and forget" but the consumer cannot be.

## When you would NOT reach for it
- Request/response systems where the client already waits synchronously — TCP and the call stack give you backpressure for free.
- Tiny in-memory pipelines where the producer and consumer demonstrably run at the same speed.
- One-shot batch jobs that finish in seconds; the engineering cost outweighs the risk.

## Key vocabulary (just enough to keep reading)
- **Producer / Publisher** — the source of data.
- **Consumer / Subscriber** — the sink that processes it.
- **Buffer** — memory that holds in-flight items between the two.
- **Demand** — how many items the consumer is currently willing to accept.
- **Drop / Shed** — discard items the system cannot handle.
- **Block** — pause the producer until capacity frees up.
- **Lag** — the gap between produced and consumed positions (Kafka's favorite metric).
- **Reactive Streams** — a cross-language spec (Java Flow, RxJS, Project Reactor) that standardizes demand-based backpressure.
- **High-water mark** — the buffer threshold that triggers a backpressure signal.

## What's next
The next document answers What / Where / When / How / Why in detail — including the Reactive Streams contract, how TCP implements backpressure beneath your socket, the math of buffer sizing, and the trade-offs between blocking, dropping, and signalling.

Sources:
- [Backpressure explained — the resisted flow of data through software (Jay Phelps)](https://medium.com/@jayphelps/backpressure-explained-the-flow-of-data-through-software-2350b3e77ce7)
- [Backpressure Patterns — Flow Control for Resilient Distributed Systems](https://codelit.io/blog/backpressure-flow-control)
- [Reactive Streams in Java: Backpressure That Works](https://medium.com/@Nexumo_/reactive-streams-in-java-backpressure-that-works-bee2816fd23e)
- [Backpressure Mechanism in Spring WebFlux (Baeldung)](https://www.baeldung.com/spring-webflux-backpressure)
- [Backpressure Handling in Streaming Systems (Conduktor)](https://www.conduktor.io/glossary/backpressure-handling-in-streaming-systems)
