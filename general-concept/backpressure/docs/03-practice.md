# Backpressure — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, backpressure is the thing standing between a healthy ingestion pipeline and a 3 AM page that reads "pod OOMKilled, restart loop, consumer lag 4M and climbing." Anywhere a Kafka topic feeds an HTTP-calling worker, anywhere a webhook ingester writes to Postgres, anywhere an S3 multipart upload streams through a transform — the design question "what happens when the next stage is slower than this one?" is a backpressure question, whether or not the team uses the word.

In data and ML platforms, it shows up as the boundary between a fast event source (CDC, telemetry, model token stream) and a slower sink (vector DB write, downstream inference, warehouse insert). LLM/agent pipelines are particularly nasty because a single planner step can generate dozens of tool calls before any of them finish — the queue between planner and executor is the whole game.

At the edge (gateways, ingress controllers, mobile clients), it shows up as TCP `rwnd`, HTTP/2 `WINDOW_UPDATE`, and the 429 your service returns when its admission control trips. None of these are exotic; they are the load-bearing primitives keeping the system alive when traffic spikes.

## Best practices

### 1. Pick the strategy from the data, not the framework
**Do:** Choose `drop`, `buffer`, `block`, or `signal` based on what the data tolerates. Telemetry and video frames: drop latest. Request queues: bounded buffer + reject. File ingestion / ETL: block the producer. Service-to-service streams: signal via Reactive Streams or HTTP/2 windows.
**Why:** The wrong strategy is silently lossy or silently fatal. Buffering metrics into a 10k-deep queue means you alert on data that is 4 minutes stale; blocking a webhook producer means the sender's retries hammer you harder.
**Avoid:** "We'll just use a queue" without naming the strategy out loud.

### 2. Every queue has a bound, written down in the constructor
**Do:** `new ArrayBlockingQueue<>(1024)`, `Channels.CreateBounded<T>(new BoundedChannelOptions(1024))`, `make(chan T, 256)`, `tokio::sync::mpsc::channel(256)`. The bound is sized to `~2–4× concurrency`, not "a big number that feels safe."
**Why:** `LinkedBlockingQueue()` with no argument defaults to `Integer.MAX_VALUE` — that is an OOM with a polite name. Even when memory holds, deep queues cause bufferbloat: throughput looks fine, p99 latency is awful, items sit waiting longer than their SLO.
**Avoid:** Unbounded queues "for now." There is no later.

### 3. Decide what `put` does when the bound is hit, and write the metric
**Do:** Pick `BoundedChannelFullMode.Wait` (block), `DropOldest`, `DropNewest`, or `DropWrite` (reject) explicitly. Increment a counter on every drop or rejection so the loss is visible in Grafana.
**Why:** A silent drop is worse than a crash because nobody notices for weeks. A queue with `dropped_total` at zero for 90 days is a queue you can trust.
**Avoid:** `try { queue.offer(x) } catch { /* ignore */ }`.

### 4. Reject with a real signal: 429 + Retry-After
**Do:** When admission control trips, return `429 Too Many Requests` with a `Retry-After` header (seconds or HTTP-date). For internal RPC, surface a typed `RESOURCE_EXHAUSTED` (gRPC code 8). Pair with a concurrency limiter (Netflix's adaptive concurrency, Envoy's `adaptive_concurrency` filter, or Vegas/Gradient2 algorithms).
**Why:** A well-behaved client backs off; a 500 or a timeout gets retried instantly with exponential rage. The retry storm after a bad rejection is the outage.
**Avoid:** Returning 200 with an empty body, or 503 with no `Retry-After`.

### 5. Propagate backpressure end-to-end, not just at the slow stage
**Do:** In a 4-stage pipeline (HTTP → queue → worker → DB), every stage signals its predecessor. If Postgres slows, the worker's bounded write pool blocks; the Kafka consumer's `poll()` slows; the producer eventually sees lag and either slows its source or sheds. Use `stream.pipeline()` in Node, `Flow` in JVM, async iterators in TS/Python — these wire the chain for you.
**Why:** Backpressure that stops at the first stage just relocates the OOM to that stage. The point is to push the decision back to the source where it can be acted on (drop low-priority traffic, ask the upstream system to slow).
**Avoid:** Adding a "smoothing buffer" in front of the slow stage. Bufferbloat is the second outage after the first one.

### 6. Use the runtime's idioms; don't reinvent the protocol
**Do:** Node.js — `stream.pipeline()` with `await` and `{ highWaterMark: 64 * 1024 }` set deliberately; respect `write()`'s return and the `drain` event. Go — bounded channels with `select { case ch <- x: default: drop() }` for shed-on-full. Rust — `tokio::sync::mpsc::channel(N)` (bounded) over `unbounded_channel()` always; `send().await` is the blocking signal. Java — `BlockingQueue.put()` to block, `offer(x, timeout)` to time out, never `add()` in a loop. .NET — `System.Threading.Channels` with `BoundedChannelFullMode`. JVM reactive — `Flowable` with explicit `onBackpressureBuffer(n, onOverflow, BackpressureOverflowStrategy.DROP_OLDEST)`, never `Observable` on hot sources.
**Why:** Hand-rolled flow control is where deadlocks and lost-wakeup bugs live. The standard library has been beaten on for a decade; trust it.
**Avoid:** Mixing a non-backpressured primitive (`Observable`, `unbounded_channel`, `EventEmitter`) into a hot path.

### 7. Treat HTTP/2 and gRPC windows as real budgets
**Do:** For long streaming RPCs, tune `initial_window_size` (default 65 KiB) and watch for `WINDOW_UPDATE` starvation. On the server, check `ServerCallStreamObserver.isReady()` before each `onNext`; in Go gRPC, respect `stream.Context().Done()` and avoid buffering ahead of the wire.
**Why:** The default 64 KiB window throttles a single fat-pipe stream to a fraction of available bandwidth. Conversely, ignoring `isReady` queues messages in user-space and you OOM behind a wire that looked healthy.
**Avoid:** "It's gRPC, it handles that for me." It handles the protocol; it does not handle your application loop.

### 8. Monitor lag, depth, and p99 — they are early warnings, not symptoms
**Do:** Expose a gauge per queue: `queue_depth`, `queue_capacity`, `queue_drops_total`, `queue_wait_seconds`. For Kafka, monitor per-partition lag (not just consumer-group total) via `kafka_consumergroup_lag` from `kafka-exporter`. Page on lag-in-seconds (lag ÷ throughput), not lag-in-messages. Watch p99 processing latency — backpressure shows up there *before* it shows up in lag.
**Why:** Aggregate lag hides a single hot partition stuck for hours. Lag-in-messages hides traffic seasonality. p99 climbing while throughput is flat is the earliest signal you'll get.
**Avoid:** Alerting on "messages/sec" alone.

### 9. Autoscale on the right signal
**Do:** For consumer workloads, autoscale on lag (KEDA's `kafka` scaler, GCP Cloud Run Kafka autoscaler, HPA with Prometheus Adapter on lag-seconds). Cap scale-out at the partition count — extra consumers in a group sit idle.
**Why:** CPU-based HPA misses I/O-bound consumers entirely; they sit at 20% CPU while lag balloons. Lag-based scaling tracks the actual SLO ("how fresh is the data").
**Avoid:** Scaling to N > partitions and wondering why it didn't help.

### 10. Make the lossy path observable and reversible
**Do:** When you drop, drop *with intent*: tag the dropped item's category, increment a labelled counter, and (for non-trivial pipelines) write a sample to a dead-letter topic. Sample 1% of drops at full fidelity.
**Why:** "We drop telemetry under load" is a fine policy if you can answer "how much, of what kind, when." It is a debugging nightmare if you can't.
**Avoid:** A swallowed exception in a `catch` block as your drop strategy.

## Anti-patterns to recognize

- **Unbounded queue + retry loop**: A `LinkedBlockingQueue()` fed by a retrying HTTP client. Under a downstream stall, retries pile in faster than work drains, the heap climbs linearly, the JVM OOMs in ~20 minutes. Better: bounded queue + circuit breaker + Retry-After on the client.
- **Swallowing 429s**: Client code catches 429 and immediately retries (often with no backoff). The signal you carefully designed becomes the next retry storm. Better: respect `Retry-After`, full jitter exponential backoff, retry budget capped at e.g. 10% of request volume.
- **Hiding backpressure behind a cache**: "Reads are slow, so we cached them." The cache fills, eviction thrashes, and the underlying DB still gets hammered on misses. Caches *amortize* load; they do not shed it. Better: explicit admission control plus the cache.
- **`highWaterMark` worship**: Bumping Node's `highWaterMark` to 1 MiB to "fix" backpressure warnings. You moved the OOM later and made GC pauses worse. Better: fix the consumer or shed at the source.
- **The smoothing buffer**: An "in-memory queue to absorb spikes" in front of the slow stage, sized at "a few seconds of traffic." Under sustained overload it is just a delayed OOM; under bursts it stretches p99 by exactly its depth. Better: bound it tight and reject above the bound.
- **Ignoring downstream errors**: Treating 5xx from a dependency as a transient blip and retrying. If the dependency is shedding *because of you*, retries make it worse. Better: classify errors (retryable vs. shed-signal) and back off on the latter.
- **Push pipelines on hot sources**: `Observable.fromEventEmitter(socket)` or RxJS Subject on a high-rate stream with no buffer strategy. Memory grows until the tab dies. Better: `Flowable` / `ReadableStream` / async iterator with explicit backpressure.
- **Alerting on absolute lag**: `lag > 10000` fires at 3 AM during normal batch traffic and is ignored during a real incident. Better: alert on `lag_seconds > SLO`, calculated as lag ÷ consumption rate.

## Real-world usage patterns

**Event ingestion at SaaS scale.** A multi-tenant analytics pipeline: HTTP ingester → Kafka → Flink → ClickHouse. The ingester runs admission control (token bucket per tenant) and returns 429 with `Retry-After` when its bounded request queue is >80% full. Kafka acts as the durable elastic buffer; Flink consumer lag is the autoscaling signal (KEDA, target lag-seconds < 30). The non-obvious lesson: the Kafka topic feels like infinite headroom, but ClickHouse merges are the real backpressure source — the Flink sink's bounded write pool is what actually holds the line.

**Streaming gRPC for live updates.** A trading dashboard pushes per-symbol price ticks over server-streaming gRPC to ~50k browsers. The server respects `isReady()` on each `StreamObserver` and *coalesces* updates per symbol while waiting — a "drop-and-replace latest" buffer per stream. Slow clients get the most recent tick, not a backlog. Lesson: per-stream backpressure beats per-connection; HTTP/2's per-stream window is the right granularity, but the application has to cooperate.

**LLM agent pipeline.** A planner LLM emits tool calls into a bounded executor channel (capacity = 2× worker concurrency). When the channel is full, the planner's emission blocks, which propagates back as token-stream backpressure to the model client. Without the bound, a single "list all 10k tickets and analyze each" plan burns through context, memory, and downstream API quota in seconds. Lesson: agent pipelines are textbook backpressure problems wearing a new costume.

**Webhook fan-out.** A CRM fans out webhook deliveries to ~100k customer endpoints. The dispatcher uses bounded per-destination queues with `DropOldest` and a circuit breaker that opens on 5 consecutive 5xx or any 429. Lesson: one slow destination must not block the others — apply backpressure at the *destination* granularity, not the global pool. A single shared queue is head-of-line blocking waiting to happen.

## Operational checklist

- [ ] Every queue, channel, and stream buffer in the code has a capacity argument; `grep` for `LinkedBlockingQueue()`, `unbounded_channel`, `make(chan` without size, `new Subject()`.
- [ ] Each bounded buffer has a metric: `depth`, `capacity`, `drops_total`, `wait_seconds`. Drops are labelled by reason.
- [ ] HTTP overload returns 429 + `Retry-After`. Internal RPC returns `RESOURCE_EXHAUSTED`. Both are tested in a load test, not just unit tests.
- [ ] Consumer lag is monitored *per partition* and alerted on lag-seconds vs. SLO, not absolute message count.
- [ ] Autoscaling is wired to the I/O signal (lag, queue depth), not just CPU. Max replicas ≤ partition count for Kafka.
- [ ] A chaos test exists where the slowest stage is artificially throttled; the system sheds visibly, p99 stays bounded, no OOM.
- [ ] Client libraries honour `Retry-After` and use full-jitter exponential backoff; retry budget is capped.
- [ ] Drops have a sample path (dead-letter topic, sampled log) so "what got shed" is answerable post-incident.
- [ ] On day one, a new engineer can find the queue capacities, the rejection metric, and the autoscaler config in under 10 minutes.

## How this topic typically evolves in a codebase

Teams start by ignoring backpressure entirely. The first version uses whatever queue primitive is closest to hand — an unbounded list, an `EventEmitter`, a `LinkedBlockingQueue`. Everything works at 10 RPS. The first real incident is almost always an OOM during a traffic spike or a downstream slowdown, and the fix is to bound the queue. This is the easy migration.

The painful migration is the *end-to-end* one: realizing that bounding the single hottest queue just moves the problem one stage upstream. Teams then introduce admission control at the edge (429 + Retry-After), wire bounded queues at every async boundary, and start monitoring lag-seconds. This is usually a multi-quarter project because it touches client libraries, autoscaler config, dashboards, and on-call runbooks simultaneously. Reactive Streams / `Flow` / async iterators tend to arrive at this stage as the way to keep the chain consistent without bespoke wiring at every hop.

The mature endpoint is backpressure as a first-class platform concern: a standard concurrency-limiter library, a standard rejection envelope, a shared dashboard template, and a chaos test in the deploy pipeline that throttles a dependency and asserts the system sheds rather than crashes. At that point engineers stop thinking about backpressure per-feature and start thinking about it the way they think about TLS — infrastructure that is just there.

## Further reading

- [Reactive Streams Specification 1.0.4](https://www.reactive-streams.org/) — the contract every JVM/JS/.NET backpressure library implements; short and worth reading directly.
- [Node.js — Backpressuring in Streams](https://nodejs.org/learn/modules/backpressuring-in-streams) — official guide; explains `highWaterMark`, `drain`, and `pipeline()` with the right level of detail.
- [BufferBloat: What's Wrong with the Internet? (Gettys & Nichols, ACM Queue 2011)](https://queue.acm.org/detail.cfm?id=2071893) — the canonical argument for why "just buffer more" is wrong; the framing still applies to application code in 2026.
- [Marc Brooker — Caution: Decreasing Returns Ahead (load shedding & admission control)](https://brooker.co.za/blog/) — the AWS principal engineer's blog has multiple posts on overload, queueing, and Little's Law that are unmatched on this topic.
- [Netflix Tech Blog — Performance Under Load (adaptive concurrency limits)](https://netflixtechblog.medium.com/performance-under-load-3e6fa9a60581) — origin story of the Vegas/Gradient2 algorithms that now underpin Envoy's adaptive concurrency filter.
- [The Backpressure Mistake We Did Not See Coming](https://medium.com/@systemdesignwithsage/the-backpressure-mistake-we-did-not-see-coming-750c4843830d) — a recent post-mortem-style walkthrough of an ingestion pipeline failure; useful as a concrete case study.
