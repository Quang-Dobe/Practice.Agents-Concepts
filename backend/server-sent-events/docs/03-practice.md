# Server-Sent Events — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

The most common place today is an AI product backend. Your API calls an LLM provider, receives its SSE stream, and relays tokens to the browser through a second SSE stream of your own. That relay owns three expensive things at once: a provider bill, a long-lived connection, and the user's patience.

The second place is the "live" corner of an ordinary SaaS app: notification bells, job progress bars, CI log tailing, admin dashboards. There, SSE is a thin layer between a message bus (Redis, Kafka, NATS) and the browser, and most of its problems come from the proxies in between rather than from the code.

The third place is high fan-out broadcast: sports scores, flash-sale counters, status pages. One event goes to tens of thousands of open connections. Here SSE stops being an endpoint and becomes its own tier with its own capacity planning.

## Best practices

### 1. Prove streaming through the real proxy chain, not localhost
**Do:** Add a smoke test that runs `curl -N` against staging through the CDN, load balancer, and reverse proxy, and asserts the first event arrives within a second. Send `X-Accel-Buffering: no`, and exclude `text/event-stream` from gzip/brotli at every hop.
**Why:** Localhost has no buffering proxy, so the bug where every event arrives in one burst at the end only shows up after deploy (see `02-deep-dive.md § Common failure modes`).
**Avoid:** Assuming that "it streams in Kestrel" means it streams for users.

### 2. Heartbeat well inside the smallest idle timeout
**Do:** Send a `:` comment roughly every 15 s. If your framework helper can't emit comments, send a named `ping` event instead. Anthropic's Messages API does exactly that.
**Why:** nginx `proxy_read_timeout` and AWS ALB both default to 60 s, and Cloudflare cuts at 100 s. Without a heartbeat, quiet streams die on schedule and reconnect in lockstep.
**Avoid:** Raising every timeout to an hour instead. One hop you forgot will still cut the stream.

### 3. Tie every subscription to cancellation, including shutdown
**Do:** Link `HttpContext.RequestAborted` with `IHostApplicationLifetime.ApplicationStopping`, and unsubscribe in a `finally` block.
**Why:** If you ignore disconnect, you get zombie subscriptions and memory that grows with churn. If you ignore shutdown, every pod blocks for the full `HostOptions.ShutdownTimeout` (30 s by default) on each deploy, and Kubernetes then kills it hard.
**Avoid:** A `while (true)` loop that only exits when a write throws.

### 4. Give each client a bounded queue and a slow-consumer policy
**Do:** Put a `Channel.CreateBounded<T>` between the bus and each connection. When it fills, drop stale updates (`DropOldest`) or close the stream and let the client resume by ID.
**Why:** One client on a bad mobile link otherwise buffers unbounded events in your process. Multiply that by a few thousand and the node runs out of memory.
**Avoid:** Writing straight from the bus callback to the socket, so a slow client stalls the publisher.

### 5. Make `id:` a real cursor into a log, and define the "too old" case
**Do:** Use IDs from a durable, ordered log (Redis Stream entry IDs, Kafka offsets). On reconnect, replay after `Last-Event-ID`. If that ID has already aged out, send a `reset` event that tells the client to refetch a snapshot.
**Why:** Without a log, `id:` promises a resume that silently drops events. Without a reset path, a client back from a laptop sleep shows stale state forever.
**Avoid:** Using a per-process counter as the ID. It restarts at 1 on every deploy and means nothing on another node.

### 6. Add a backplane the day you run a second instance
**Do:** Publish events to Redis Pub/Sub, NATS, or Kafka, and have every SSE node subscribe to it.
**Why:** With an in-memory subscriber list behind a load balancer, about half your users stop getting events the moment you scale to two pods.
**Avoid:** Sticky sessions as the fix. They pin connections but don't route events produced on another node.

### 7. One multiplexed stream per tab, over HTTP/2
**Do:** Open a single `EventSource` per tab that carries all topics, and route events with `event:` names. Serve browsers over HTTP/2 or HTTP/3.
**Why:** On HTTP/1.1, the ~6-connections-per-origin limit is shared across tabs, so the seventh tab hangs. Even on HTTP/2, nginx usually speaks HTTP/1.1 upstream, so each stream is still one upstream TCP connection.
**Avoid:** One `EventSource` per widget.

### 8. Authenticate the connection, then authorize every event
**Do:** Use cookies with `withCredentials`, or a short-lived (~60 s) single-use ticket in the query string that you exchange at connect time. Filter fan-out by tenant and user before writing each event. Cap open streams per user.
**Why:** A long-lived bearer token in the URL ends up in access logs. A topic-wide broadcast that skips per-event checks leaks one tenant's data to another.
**Avoid:** Checking auth only at connect and then trusting a `?channel=` parameter supplied by the client.

### 9. Let a formatter write the wire format
**Do:** Use `TypedResults.ServerSentEvents` / `SseFormatter` (.NET 10) or your stack's equivalent, and JSON-serialize payloads, which escapes newlines.
**Why:** A raw `\n` in user text written into one `data:` line ends the event early. That lets an attacker inject fake `event:`/`id:` lines.
**Avoid:** `$"data: {message}\n\n"`.

### 10. Handle the permanent-failure path on the client
**Do:** Watch for `readyState === CLOSED`. That state follows a non-200 response such as an expired-auth 401. Refresh credentials, then recreate the `EventSource` with jittered backoff, and pass the last seen ID as a query parameter.
**Why:** The browser auto-retries only network drops. After a 401 the stream stops for good and the UI looks frozen with no error shown.
**Avoid:** Treating every `error` event as fatal. It also fires on each normal reconnect.

### 11. Size per-node capacity with a load test, not a guess
**Do:** Load-test with a non-browser client (Shopify used `okhttp-eventsource`) and record connections per pod at your memory and CPU ceiling. Check file-descriptor limits, `net.core.somaxconn`, and proxy ephemeral ports.
**Why:** The limits are usually in the OS, not in SSE. Linux's default port range (32768–60999) gives one proxy about 28K concurrent connections to one upstream IP:port.
**Avoid:** Autoscaling on CPU alone. Idle SSE connections use memory and descriptors, not CPU.

## Anti-patterns to recognize

- **The static subscriber list**: a `static ConcurrentDictionary` of open responses that the publish endpoint loops over. It works perfectly with one instance and silently loses events with two. Put a backplane in from the first deploy that has more than one replica.
- **The "helpful" reconnect wrapper**: `onerror = () => { es.close(); es = new EventSource(url); }`. It throws away the browser's `Last-Event-ID`, skips its retry delay, and turns a blip into a reconnect storm. Let the browser reconnect on its own, and rebuild only on `CLOSED`.
- **IDs without memory**: the server emits `id:` because a tutorial did, but it keeps no log. Reconnects look healthy in dashboards while users miss events. Either back the IDs with a log or omit them, so nobody assumes resume works.
- **Orphaned upstream generation**: an LLM relay that keeps reading from the provider after the browser disconnects. You pay for every token nobody reads, and on a busy chat product that is a visible share of the bill. Propagate cancellation upstream, unless you deliberately chose resumable generation (see the LLM pattern below).
- **SSE on the API pods**: the stream endpoints share pods with an API you deploy 20 times a day. Every deploy disconnects every user at once, and they all hit the new pods together. Move streams to a separate, rarely deployed tier, or drain pods gradually with jittered `retry:`.
- **Fat broadcast, thin authorization**: you push full records to a topic channel because "everyone on this page can see it anyway". The first shared dashboard across tenants leaks data. Either filter per subscriber, or send thin events ("order 123 changed") that make the client refetch through normal authorization.

## Real-world usage patterns

**LLM chat relay.** A product backend sits between the browser and a model provider and turns provider SSE events into its own simpler stream. Two camps exist on disconnects. *Cancel-on-disconnect* aborts the upstream call to save tokens. *Resumable generation*, as in the Vercel AI SDK with the Redis-backed `resumable-stream`, keeps generating into Redis so a page reload can reattach. The AI SDK docs warn that in this mode, client aborts are only disconnects and must not cancel generation. The tie-breaker is task length: for answers that take seconds, cancel; for agent runs that take minutes, resume. Lesson: decide this up front, because a separate "stop" endpoint and the resume mode interact in non-obvious ways.

**Flash-sale live map.** [Shopify's BFCM Live Map](https://shopify.engineering/server-sent-events-data-streaming) replaced a design where clients polled a mailbox at least every 10 s. In the new one, Flink writes to Kafka, and a Go SSE server behind nginx pushes each update the moment it arrives. Clients sent JWTs through an `EventSource` polyfill, because native `EventSource` can't set headers. Lesson: the SSE tier stayed stateless and scaled horizontally because Kafka was the log. Capacity per pod came from a load-test client, not from estimates.

**Messaging at social-network scale.** [LinkedIn's instant messaging](https://www.linkedin.com/blog/engineering/archive/instant-messaging-at-linkedin-scaling-to-hundreds-of-thousands-) chose SSE over WebSockets for compatibility and reached 100K persistent connections per node. Every wall they hit was outside the protocol: `somaxconn` at 128, a 30K file-descriptor limit (raised to 200K), and roughly 20 KB of TLS state per connection, which pushed the heap to 16 GB. Lesson: SSE capacity planning is kernel and memory tuning.

**Enterprise B2B behind corporate proxies.** A [team's postmortem](https://dev.to/miketalbot/server-sent-events-are-still-not-production-ready-after-a-decade-a-lesson-for-me-a-warning-for-you-2gie) describes one customer's legacy proxy that held the chunked stream until it closed. Logins took 20 minutes, and server metrics were all green. Their fix had the client acknowledge receipt, and if no ack arrived within seconds, the server closed the stream, which forced the proxy to flush. Lesson: you can't see client-side buffering from the server. Measure time-to-first-event on the client.

## Operational checklist

- Do dashboards show open connections per node, connection opens per second, and a **connection-duration histogram**? A spike at exactly 60 s or 100 s points to an intermediary timeout.
- Is publish-to-render latency measured, with a server timestamp in the payload and the delta reported by the client?
- Is the heartbeat interval shorter than the smallest idle timeout in the chain, and is that chain written down?
- Does a staging smoke test fail when any hop starts buffering or compressing `text/event-stream`?
- Has the "pod killed mid-stream" case been tested: do clients resume on another node with no gaps and no duplicates?
- When `Last-Event-ID` is older than retention, does the client get a reset/snapshot rather than silence?
- Security: are tokens kept out of long-lived URLs, is authorization checked per event, and are streams per user capped?
- Cost: does a client disconnect cancel upstream work (LLM calls, DB cursors)? Are SSE endpoints kept off duration-billed serverless runtimes?
- Onboarding: does the README say "one `EventSource` per tab, named events, never recreate it on `error`"?

## How this topic typically evolves in a codebase

Teams start with one endpoint and an in-memory list of subscribers, often added for a single feature like a progress bar. It works, so a second feature reuses it. Then the service scales to two replicas, half the users miss events, and the first painful fix is a Redis Pub/Sub backplane. Pub/Sub is fire-and-forget, so reconnect gaps remain. The next step moves to Redis Streams or Kafka so that `id:` finally means something.

The expensive migration is retrofitting real event IDs and replay. Every producer has to write to one ordered log. Every client has to handle a `reset` event. And old clients that cached their own counter-based IDs send cursors that no longer mean anything. Teams that design IDs and snapshot-plus-delta from day one skip this rewrite.

At larger scale, streaming splits into its own tier: a dedicated SSE gateway with its own deploy schedule, autoscaling on connection count, and kernel tuning. Alternatively, it moves to a hub such as Mercure or a managed push service. The trigger is usually deploy pain: once a routine API deploy disconnects every user, keeping streams on separate infrastructure pays for itself.

## Further reading

- [WHATWG HTML — Server-sent events](https://html.spec.whatwg.org/multipage/server-sent-events.html): the normative parsing and reconnect rules. Read it once to settle every "what does the browser do when…" argument.
- [Eric Lawrence — The Pitfalls of EventSource over HTTP/1.1](https://textslashplain.com/2019/12/04/the-pitfalls-of-eventsource-over-http-1-1/): why the connection limit is permanent, from a former browser engineer.
- [LinkedIn — Instant Messaging: Scaling to Hundreds of Thousands of Persistent Connections on One Machine](https://www.linkedin.com/blog/engineering/archive/instant-messaging-at-linkedin-scaling-to-hundreds-of-thousands-): the best concrete list of OS-level limits you will hit.
- [Shopify — Using Server Sent Events to Simplify Real-time Streaming at Scale](https://shopify.engineering/server-sent-events-data-streaming): the Kafka-backed SSE tier, load testing, and auth through a polyfill.
- [Vercel AI SDK — Chatbot Resume Streams](https://ai-sdk.dev/docs/ai-sdk-ui/chatbot-resume-streams): the resumable-generation design and its trade-off with abort.
- [ASP.NET Core 10 release notes](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0): `TypedResults.ServerSentEvents` and `SseItem<T>`, the .NET-native serving path.
