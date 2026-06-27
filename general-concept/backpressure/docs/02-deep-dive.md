# Backpressure — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
Backpressure is **feedback-driven flow control between an asynchronous producer and an asynchronous consumer**, in which the consumer's processing capacity is propagated upstream so the producer adjusts its emission rate. It is a property of the *protocol* between two stages, not a property of either stage in isolation. Where flow control answers "how much can I send?", backpressure is the specific subclass where the answer travels *backwards* through the pipeline, hop by hop, and eventually reaches the original source.

The canonical formal statement comes from the Reactive Streams spec (1.0.3, 2017): *the total number of `onNext` signals emitted by a Publisher MUST be less than or equal to the total number of elements requested by the Subscriber via `Subscription.request(n)` at all times*. That single invariant is the contract.

### The core building blocks
- **Producer / Publisher** — the source. Emits items asynchronously.
- **Consumer / Subscriber** — the sink. Has finite processing capacity per unit time.
- **Channel / Subscription** — the bidirectional link. Carries data downstream and demand or credit upstream.
- **Demand (a.k.a. credit, window)** — an integer the consumer publishes representing items it is currently willing to accept. The producer treats it as a hard ceiling.
- **Buffer / queue** — bounded memory between the two. Its size and high-water mark determine when the demand signal fires.
- **Strategy** — what happens when demand is zero and items are still arriving: `buffer`, `drop`, `block`, or `signal-upstream`.
- **Termination signals** — `onComplete` (done), `onError` (failed), `cancel` (consumer no longer interested). Backpressure protocols are not complete without these.

### How it relates to the broader landscape
Backpressure sits inside the larger family of **flow control**, alongside **congestion control** (network-wide, e.g. TCP Reno, BBR), **rate limiting** (open-loop caps applied at the edge), and **load shedding** (degraded modes under overload). Within flow control, the two dominant mechanism families are **window-based** (TCP receive window, HTTP/2 `WINDOW_UPDATE`) and **credit-based / demand-based** (Reactive Streams `request(n)`, on-chip interconnects, InfiniBand). The two families are mathematically similar — both bound in-flight units by a number the receiver controls — but differ in where the bookkeeping lives and how granular the unit is (bytes vs. logical messages).

## Where

### Where it runs / lives in the stack
Backpressure appears at every layer where an asynchronous boundary exists:

- **L4 transport:** TCP receive window, advertised in every ACK.
- **L7 application transport:** HTTP/2 stream and connection flow-control windows; gRPC inherits this directly.
- **Runtime/stream libraries:** Node.js streams, Java Flow API, Project Reactor, RxJS, Akka Streams.
- **Message brokers:** Kafka consumer pull, RabbitMQ prefetch (`basic.qos`), Pulsar receiver queue.
- **In-process channels:** Go bounded channels, Rust `tokio::sync::mpsc`, Java `BlockingQueue`.
- **UI event loops:** browser frame scheduling, drag/scroll throttling.

If two components run at different speeds and exchange items, there is a backpressure decision there, whether or not anyone designed for it.

### Where you typically encounter it
- **TCP sockets** — every socket you write to is silently exerting backpressure on you via the kernel send buffer.
- **Node.js streams** — `writable.write()` returns `false` when the internal buffer crosses `highWaterMark` (default 16 KiB for byte streams, 16 items for object mode).
- **gRPC server streaming** — HTTP/2 default initial window of 65,535 bytes per stream; servers expose `isReady()` on the response observer.
- **Kafka consumers** — pull-based; `max.poll.records` (default 500) and `fetch.max.bytes` cap each poll.
- **Project Reactor / RxJava** — `Flux`/`Flowable` carry demand; `Observable` does not (which is the entire reason `Flowable` exists in RxJava 2+).
- **Akka Streams** — the runtime negotiates `Pull` and `Push` GraphStage signals automatically; user code declares the graph, not the backpressure logic.

### Ecosystem and tooling
- **Specs:** Reactive Streams 1.0.4 (JVM, JS, .NET), JEP 266 (`java.util.concurrent.Flow`, Java 9+), WHATWG Streams (browser, with `ReadableStream`/`WritableStream` and built-in backpressure via `desiredSize`).
- **JVM stream libraries:** Project Reactor, RxJava 3, Akka Streams, Mutiny (Quarkus), Vert.x streams.
- **JS:** RxJS (push, no native backpressure), Node `stream`, web `ReadableStream`, async iterators (pull, native backpressure).
- **.NET:** `IAsyncEnumerable<T>` (pull), `System.Threading.Channels` (bounded with `BoundedChannelFullMode`), Reactive Extensions for .NET.
- **Networking:** TCP itself, HTTP/2 (`WINDOW_UPDATE` frame), QUIC (per-stream and connection-level limits), gRPC.
- **Brokers:** Kafka (pull), RabbitMQ (prefetch credits), Pulsar (receiver queue size), NATS JetStream (consumer max-ack-pending).

## When

### When the topic emerged and why
The idea is as old as flow-controlled networking — XON/XOFF on serial lines in the 1960s, sliding windows in TCP (RFC 793, 1981). The *modern* programming-language conversation crystallized around 2013–2015 with the Reactive Streams initiative (Netflix, Lightbend, Pivotal, Red Hat). The motivation was concrete: Java applications adopting RxJava 1.x discovered that `Observable` had no demand signal, and any source faster than the sink reliably caused `MissingBackpressureException` or OOMs. The spec was promoted to a JDK API in Java 9 as `java.util.concurrent.Flow`.

The infrastructure conversation re-ignited around the same time with Jim Gettys' 2011 ACM Queue article *BufferBloat: What's Wrong with the Internet?*, which framed unmanaged buffering as a global latency problem, not a local correctness one.

### When to use it in a project
Reach for explicit backpressure when:
- The pipeline has an **asynchronous boundary** (queue, channel, socket) where producer and consumer run on independent schedules.
- The **producer rate is unbounded or bursty** (event streams, IoT telemetry, log tailing, change-data-capture).
- The **consumer's cost per item is variable** (DB writes, downstream HTTP calls, ML inference).
- You care about **bounded memory** more than zero data loss — i.e. the system must keep running.
- You are **chaining stages**; without end-to-end demand propagation, the slowest stage's backpressure stops at its own input, and earlier stages keep allocating.

### When NOT to use it
Avoid building explicit backpressure machinery when:
- The pipeline is **synchronous request/response** — the call stack is your backpressure.
- The producer is **strictly slower** than the consumer in all realistic conditions.
- The data is **inherently lossy and time-sensitive** (live video frames, mouse positions); a fixed-size buffer with drop-latest is simpler than a demand protocol.
- You are tempted to build it on top of an **unbounded queue** "for now." That is not backpressure; that is a memory leak with a polite name.

## How

### How it works under the hood
A demand-based pipeline (the Reactive Streams model) cycles like this:

1. **Subscribe.** Consumer calls `publisher.subscribe(subscriber)`. Publisher invokes `subscriber.onSubscribe(subscription)`, handing back a control handle. No data flows yet.
2. **Initial demand.** Subscriber calls `subscription.request(n)` — say, `request(32)`. This is the credit grant.
3. **Bounded emission.** Publisher emits up to 32 `onNext(item)` calls. It MUST NOT exceed the outstanding demand. If its source cannot be slowed (clock ticks, mouse events), it must internally buffer or drop to respect the bound.
4. **Replenishment.** As the subscriber processes items, it issues further `request(k)` calls. Pipelined demand keeps the channel full without round-trips per item.
5. **Termination.** Publisher emits `onComplete()` or `onError(t)`. Or the subscriber calls `subscription.cancel()` and the publisher releases resources.

The **TCP analogue** is structurally identical at the byte level. The receiver advertises a `rwnd` (receive window) in every ACK; the sender keeps in-flight bytes ≤ `min(rwnd, cwnd)` where `cwnd` is the congestion window. The unit is bytes, not messages. Window scaling (RFC 7323) lifts the original 16-bit window cap to 1 GiB.

**HTTP/2 / gRPC** layers per-stream and per-connection windows over TCP. Default initial window: 65,535 bytes per stream. As the receiver consumes bytes, it sends `WINDOW_UPDATE` frames to extend the sender's budget. gRPC adds a higher-level `isReady` signal so application code can pause emission before the HTTP/2 layer engages.

**Node.js streams** use a hybrid. A writable stream maintains an internal buffer; `write()` returns `true` while the buffer is below `highWaterMark` and `false` once exceeded. Crucially, returning `false` does not stop the write — it is advisory. Well-behaved producers call `pause()` on the source and resume when the `drain` event fires. `pipe()` wires this for you.

**Kafka** inverts the model: brokers never push. Consumers call `poll(duration)`, receiving up to `max.poll.records` per partition assignment. Backpressure is implicit in the consumer's poll cadence. If processing is slow, the consumer simply polls less often; the broker doesn't care, because the data is durable on disk. The trade-off is **consumer lag** — measured as the offset gap between log head and committed offset.

**Akka Streams** runs a dynamic push-pull negotiation. When demand exists upstream and a downstream stage is fast, the graph effectively pushes. When the downstream slows, the same graph effectively pulls. The user never writes `request(n)`; the runtime materialises the protocol from the declared graph.

### Key trade-offs

| Design choice | Gained | Given up |
|---|---|---|
| **Buffer** unbounded | Never lose data, never block | Unbounded memory; latency grows with queue depth (bufferbloat) |
| **Buffer** bounded | Bounded memory, smooths short bursts | Must pick a strategy at the boundary; tuning required |
| **Drop** (newest or oldest) | Constant memory, low latency, lossy by design | Loses data; need consumer tolerance for gaps |
| **Block** producer | Lossless, simple to reason about | Backpropagates stalls; risks deadlock if producer holds shared resources |
| **Signal upstream** (demand) | Lossless, lets the producer decide what to do with its own source | Requires cooperation from every stage; adds protocol complexity |
| **Push** (observable) | Low per-item overhead, natural for events | Hard to add backpressure later; requires consumer-side buffer |
| **Pull** (iterator, Kafka) | Backpressure is automatic | Per-item RTT cost unless batched; consumer must remember to ask |
| **Credit-based** (bytes/messages) | Tight buffer bounds at every hop | Bookkeeping per hop; harder across high-RTT links |

### Common failure modes
- **Unbounded queue** — producer fills it forever, OOM eventually. Cause: choosing a queue without choosing a bound.
- **Bufferbloat** — bounded but enormous buffer; throughput looks fine, p99 latency is awful. Cause: confusing buffering with absorption.
- **Silent drop** — the `Observable` or UDP socket discards under load with no metric. Cause: missing observability around the drop path.
- **Head-of-line blocking** — one slow consumer on a shared channel stalls all others. Cause: applying backpressure at the wrong granularity (connection instead of stream).
- **Deadlock via cyclic demand** — A waits on B's demand, B waits on A's. Cause: graphs with cycles and no buffered cut-edge.
- **Demand storm / 1-at-a-time** — subscriber calls `request(1)` per item, network RTT dominates. Cause: not amortising the demand signal.
- **Lag without alerts** — Kafka consumer slows; messages still durable; alert fires hours later. Cause: monitoring throughput instead of lag.
- **`highWaterMark` ignored** — Node.js code writes in a loop without checking `write()`'s return value. Cause: the API is advisory, not enforcing.

## Why

### Why it exists
Two asynchronous components running at different rates is the default case in distributed systems, not the exception. Without a feedback channel, the only places the mismatch can land are **memory** (grows without bound), **latency** (queues stretch), or **the floor** (drops, crashes). Backpressure is the engineering decision to **make that landing site explicit and bounded** rather than emergent. It is the same impulse that produced TCP windows: the network of the 1970s would have collapsed without per-receiver flow control, and a Node.js process today collapses for the same reason on a smaller scale.

### Why it looks the way it does
The obvious alternative to demand signalling is **rate-based control** — the producer is told "emit at most N items/sec" and self-regulates. Rate-based works well when capacity is stable and predictable, which is why it dominates ingress rate limiting at API gateways. It works poorly *inside* a pipeline because consumer capacity is rarely constant: a DB writer's throughput depends on the row, the index state, lock contention, GC pauses. A demand signal tracks instantaneous capacity automatically; a rate cap requires you to re-tune whenever conditions change.

The second non-obvious choice is **lossless by default**. The Reactive Streams spec forbids the publisher from exceeding requested demand, even for sources that cannot be slowed (clock ticks). This forces the publisher author to confront the lossy/lossless question at the source rather than letting it leak downstream as `MissingBackpressureException`s. The price is more code at the producer; the gain is that every downstream stage can assume the invariant holds.

The third is **batched credit grants** (`request(n)` for n > 1, TCP windows in KB not bytes). A stop-and-wait protocol where every item requires a round-trip is correct but pathologically slow on any link with non-trivial RTT. Batching amortises the control overhead; that is also why Kafka consumers fetch in records-per-poll batches, not one at a time.

### Why it matters now
Three trends in 2026 keep this topic in front of working engineers:

1. **Streaming-first architectures.** Kafka, Pulsar, Flink, Materialize, and the broader CDC/event-driven stack are now mainstream rather than specialist tooling. Every one of them is a backpressure problem dressed as a product.
2. **LLM and agent pipelines.** Token streams from one model feeding another, retrieval feeding generation, agent step queues — these are exactly the asynchronous boundaries where unbounded buffering shows up as OOMs in production.
3. **Edge and serverless.** Per-instance memory limits are tighter (128–512 MiB on many platforms), and the cost of getting backpressure wrong is being killed by the platform rather than slowed down.

The fundamentals have not changed since RFC 793. The number of places engineers have to apply them has.

## Open questions / things to verify in practice
- What is the actual `highWaterMark` of every stream in your Node.js pipeline, and do you check `write()`'s return value at each boundary?
- For a streaming gRPC method, is the server respecting `isReady()`, or queuing in user-space ahead of the HTTP/2 window?
- On your Kafka consumers, is processing latency tracked alongside lag, so a slow batch is visible before lag balloons?
- For your RxJava code, are you on `Flowable` (backpressure-aware) or `Observable` (not), and is that intentional per stream?
- Are any "queues" in your system genuinely unbounded? Search the codebase for `LinkedBlockingQueue()` with no capacity argument — that constructor defaults to `Integer.MAX_VALUE`.
- For lossy streams (telemetry, metrics), is `onBackpressureLatest` or a coalescing buffer in place, with a counter on dropped items so the loss is observable?
