# Server-Sent Events — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Server-Sent Events (SSE) is the pairing of a wire format, `text/event-stream`, with a client processing model, the `EventSource` interface. Both are defined in the [WHATWG HTML Living Standard, section "Server-sent events"](https://html.spec.whatwg.org/multipage/server-sent-events.html). The W3C published a standalone [Server-Sent Events Recommendation](https://www.w3.org/news/2015/server-sent-events-is-a-w3c-recommendation/) on 3 February 2015. It is now marked superseded, and the WHATWG text is the normative one.

Formally, SSE is a unidirectional, server-to-client message stream carried in the body of a single, long-lived HTTP `GET` response. Framing is UTF-8 and line-oriented. The client owns reconnection, and the server can supply a resume cursor (`id`) that the client echoes back on reconnect.

### The core building blocks

- **`text/event-stream` MIME type.** Always UTF-8; the spec says "there is no way to specify another character encoding". A leading BOM is stripped. Lines may end in CRLF, LF, or a bare CR.
- **Line grammar.** A line starting with `:` is a comment. A line containing `:` splits into field name and value, and one leading space is removed from the value. A line with no colon is a field name with an empty value. Unknown field names are ignored, which is how the format stays forward-compatible.
- **The four fields.**
  - `data` appends its value plus an LF to the data buffer.
  - `event` sets the event type.
  - `id` sets the last-event-ID buffer, unless the value contains U+0000 NULL, in which case the field is ignored.
  - `retry` sets the reconnection time in milliseconds, but only if the value is all ASCII digits.
- **Dispatch on blank line.** A blank line ends the event. The trailing LF is stripped from the data buffer. If the buffer is empty, nothing is dispatched. Otherwise a `MessageEvent` fires with type `message`, or the `event` value if one was set.
- **Sticky last event ID.** The ID buffer is *not* cleared between events. An event without an `id:` line inherits the previous ID. An empty `id:` line resets it.
- **`EventSource` interface.** It has `url`, `withCredentials` (false by default), `readyState` (`CONNECTING = 0`, `OPEN = 1`, `CLOSED = 2`), the `open`, `message`, and `error` events, and `close()`.
- **`Last-Event-ID` request header.** On reconnect, the browser sends it with the stored ID if that ID is non-empty.
- **Reconnection time.** The spec leaves the default to the implementation ("probably in the region of a few seconds"). Chromium and WebKit hard-code `defaultReconnectDelay = 3000` ms in `EventSource.cpp`.

### How it relates to the broader landscape

SSE belongs to the **server-push** family that replaced "Comet" hacks. All of these work around the same constraint: an HTTP server cannot start a message on its own.

| Technique | Direction | Transport | Reconnect / resume | Binary |
|---|---|---|---|---|
| Short polling | pull | new request per interval | n/a | yes |
| Long polling | pull, one message per request | new request per message | app-defined | yes |
| **SSE** | server to client | one HTTP response | built in (`retry`, `Last-Event-ID`) | no (UTF-8 only) |
| WebSocket ([RFC 6455](https://www.rfc-editor.org/rfc/rfc6455)) | both | HTTP Upgrade, then own framing | app-defined | yes |
| WebTransport | both, multi-stream, datagrams | HTTP/3 / QUIC | app-defined | yes |
| Web Push ([RFC 8030](https://www.rfc-editor.org/rfc/rfc8030)) | server to service worker | vendor push service | vendor-managed | encrypted payload |

## Where

### Where it runs / lives in the stack

SSE is an application-layer (L7) convention on top of HTTP semantics. It has no transport of its own.

- On **HTTP/1.1**, the response body is usually sent with `Transfer-Encoding: chunked`, and each flush becomes one or more chunks. The stream occupies the whole TCP connection.
- On **HTTP/2** ([RFC 9113](https://www.rfc-editor.org/rfc/rfc9113)), the stream is a sequence of DATA frames on one stream of a multiplexed connection. On **HTTP/3**, it is one QUIC stream, so a lost packet on another stream does not stall it.

On the server, an SSE endpoint is ordinary request-handler code that writes and flushes without returning. Every intermediary between client and origin (CDN, WAF, load balancer, reverse proxy, compression middleware) has to pass bytes through as they arrive. That requirement causes most production incidents.

### Where you typically encounter it

- **LLM APIs.** OpenAI Chat Completions streams data-only events and ends with `data: [DONE]`. The [Anthropic Messages API](https://platform.claude.com/docs/en/build-with-claude/streaming) uses named events: `message_start`, `content_block_start`, `content_block_delta`, `content_block_stop`, `message_delta`, `message_stop`, plus `ping` and `error`.
- **Model Context Protocol.** [Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http) answers a `POST` with either JSON or a `text/event-stream` response scoped to that request.
- **Mercure.** A pub/sub hub protocol built on SSE with JWS authorization, published as an IETF Internet-Draft ([draft-dunglas-mercure](https://datatracker.ietf.org/doc/draft-dunglas-mercure/)).
- **Live dashboards, CI build logs, and notification feeds.** These are classic one-way fan-out with low write rates.

### Ecosystem and tooling

- **Serving from .NET.**
  - `TypedResults.ServerSentEvents(IAsyncEnumerable<T>)` is new in [ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0) and works in Minimal APIs and controllers.
  - `SseItem<T>` carries `Data`, `EventType`, `EventId`, and `ReconnectionInterval`.
  - `SseFormatter` (.NET 10) handles the field serialization.
- **Serving from other stacks.** Spring has `SseEmitter`. Python has `sse-starlette`. In Node you write to `res` and flush.
- **Consuming outside the browser.**
  - .NET has `SseParser` in the [`System.Net.ServerSentEvents`](https://learn.microsoft.com/dotnet/api/system.net.serversentevents) package (first shipped with .NET 9; also targets `netstandard2.0`).
  - Node has undici's spec-compliant `EventSource`, exposed globally behind `--experimental-eventsource` (status depends on the Node version).
- **Browser clients beyond `EventSource`.**
  - `@microsoft/fetch-event-source` adds `POST`, custom headers, and custom retry handling on top of `fetch`.
  - `eventsource-parser` is a parser alone, for when you bring your own transport.
- **Multi-node fan-out.** Redis Pub/Sub, Redis Streams (which add replay), NATS, or Kafka sit behind the SSE nodes. A Mercure hub can take over the job entirely.
- **Debugging.** `curl -N` turns off curl's output buffering. Chrome DevTools shows an "EventStream" tab for `EventSource` requests.

## When

### When the topic emerged and why

Ian Hickson specified SSE in the WHATWG "Web Applications 1.0" draft starting in 2004. Opera 9 shipped a prototype in September 2006 as an `<event-source>` *element*. Feedback pushed a redesign into a script object modelled on `XMLHttpRequest`. Safari, Chrome, and Firefox shipped `EventSource` between 2010 and 2011. Internet Explorer never did; Microsoft support arrived with Chromium-based Edge 79 in 2020.

Before SSE, push meant Comet: long polling, "forever iframe" streaming, XHR streaming with home-made framing, or Flash sockets. Every team reinvented framing, parsing, and reconnect logic. WebSocket (RFC 6455, December 2011) arrived almost at the same time and took most of the attention.

SSE's revival had two causes. First, HTTP/2 (2015) removed its biggest practical limit, the HTTP/1.1 connection pool. Second, starting around 2023, LLM APIs needed to stream tokens one way, which is exactly SSE's shape.

### When to use it in a project

Reach for it when:
- Data flows overwhelmingly server to client, and client actions fit normal `POST`/`PUT` requests.
- Payloads are text or JSON.
- You want push to pass through existing HTTP infrastructure (auth middleware, cookies, CORS, HTTP/2, observability) without a protocol upgrade.
- Clients can tolerate reconnect gaps, or you can keep a replay log keyed by event ID.
- You serve over HTTP/2 or HTTP/3, or each client needs only one stream.

### When NOT to use it

Avoid it when:
- Both directions are chatty and latency-sensitive (games, collaborative cursors). Pairing each upstream message with its own `POST` adds a request per message. Use WebSocket or WebTransport.
- Payloads are binary. Base64 inflates them by about 33% and costs CPU.
- You need guaranteed delivery with acknowledgements. SSE has no ack channel, so you would be building a message broker by hand.
- Delivery must reach users whose page is closed. Use Web Push.
- Your platform cuts long responses short, for example serverless functions with hard execution limits or gateways with fixed response timeouts. Per-request SSE (the MCP model) still works there; long-lived subscription streams do not.

## How

### How it works under the hood

1. **Construct.** `new EventSource(url, { withCredentials })` sets `readyState = CONNECTING` and issues a `GET` with `Accept: text/event-stream` and cache mode `no-store`. Cross-origin requests follow CORS, and `withCredentials: true` maps to credentials mode `include`.
2. **Validate the response.** If the status is not `200` or the `Content-Type` is not `text/event-stream`, the browser *fails the connection*: `readyState = CLOSED`, an `error` event fires, and it does not reconnect. A `204 No Content` is the conventional way for a server to say "stop retrying". `301` and `307` redirects are followed.
3. **Open.** `readyState = OPEN` and `open` fires.
4. **Parse incrementally.** Bytes are decoded as UTF-8 and split into lines. Lines fill the data, event-type, and ID buffers. A blank line dispatches a `MessageEvent` whose `data`, `origin`, and `lastEventId` are populated.
5. **Server writes and flushes.** Each event is serialized and *flushed*. Without the flush, it sits in the server or proxy buffer. The server may interleave `:` comment lines as heartbeats; the spec suggests one about every 15 seconds for old proxies.
6. **Drop.** On a network error or a clean server close, `readyState` returns to `CONNECTING` and `error` fires. The browser waits the reconnection time (the `retry` value, or the default). The spec allows exponential backoff after failed attempts. It then re-issues the `GET` with `Last-Event-ID`.
7. **Resume.** The server reads `Last-Event-ID` and replays everything after it from a durable log (a ring buffer, Redis Stream, or Kafka offset), then continues live. Without such a log the header is useless.
8. **Terminate.** The client calls `close()`, or the server answers a reconnect with `204` or any non-200 status.

```text
             new EventSource()
                    |
                    v
   +---------> CONNECTING --(non-200 / wrong type / 204)--> CLOSED
   |                |                                         ^
   | wait retry ms  | 200 + text/event-stream                 |
   | + send         v                                         |
   | Last-Event-ID OPEN --- blank line => dispatch event      |
   |                |                                         |
   +--(drop/EOF)----+------------(close())--------------------+
```

A multi-line payload shows the parsing rules:

```text
id: 7
event: log
data: line one
data: line two

: heartbeat

```

This dispatches one `log` event with `data === "line one\nline two"` and `lastEventId === "7"`. The comment line dispatches nothing.

**Server cost model.** In async servers such as Kestrel or Node, an idle SSE client costs one socket plus a small buffer and a subscription entry. It does not hold a thread. Fan-out across instances needs a backplane, because the client that needs event N may be connected to a different node from the one that produced it. Kestrel caps HTTP/2 streams per connection (`Http2Limits.MaxStreamsPerConnection`, default 100). RFC 9113 recommends advertising no fewer than 100 concurrent streams.

### Key trade-offs

| Design choice | Gained | Given up |
|---|---|---|
| Plain HTTP response, no Upgrade | Works through HTTP auth, CORS, HTTP/2/3, and standard proxies; debuggable with `curl` | Every buffering intermediary is a hazard; no protocol-level ping |
| UTF-8 text framing | Human-readable; trivial to write and parse | No binary; base64 overhead; per-event field overhead |
| One-way only | Simple server model; upstream reuses normal REST | Request/response correlation across channels is your problem |
| Client-owned reconnect | Free resilience in browsers; stateless server can resume via cursor | Synchronized reconnect storms; replay needs a log you build |
| `EventSource` is `GET`-only, no custom headers | Safe to auto-replay (idempotent request) | Bearer tokens go in cookies or query strings, or you switch to a `fetch`-based client and lose built-in reconnect |
| Long-lived connection | Latency about one network hop, no polling waste | Connections pin to nodes; scale-out does not rebalance existing clients; deploys drop everyone at once |

### Common failure modes

- **Events arrive in one burst at the end.** Cause: response buffering. nginx `proxy_buffering` is on by default, and compression middleware and missing `Flush` calls have the same effect. Fix with `X-Accel-Buffering: no` or `proxy_buffering off`, and exclude `text/event-stream` from gzip/brotli.
- **The stream dies at exactly 60 or 100 seconds.** Cause: idle timeouts. nginx `proxy_read_timeout` defaults to 60 s, AWS ALB idle timeout to 60 s, and Cloudflare's proxy read timeout to 100 s. Send heartbeats well inside the smallest one.
- **The seventh tab hangs forever.** Cause: the HTTP/1.1 limit of about 6 connections per origin, across all tabs. Chrome and Firefox marked this "Won't fix" ([Eric Lawrence's write-up](https://textslashplain.com/2019/12/04/the-pitfalls-of-eventsource-over-http-1-1/)).
- **CPU spikes right after a deploy.** Cause: every client reconnects after the same fixed delay (3 s in Chromium). Spread restarts out, and vary `retry` per client to add jitter.
- **Gaps after reconnect.** Cause: the server emits `id:` but has no replay log, or it emits no `id:` at all.
- **Injected or truncated events.** Cause: user text containing `\n` was written into a single `data:` line. The server must split on newlines and emit one `data:` line per piece.
- **Server memory grows with churn.** Cause: the handler ignores client disconnect (`HttpContext.RequestAborted` / `CancellationToken`), leaving zombie subscriptions.
- **`onmessage` never fires.** Cause: the server sets `event: foo`. Named events only reach `addEventListener("foo", …)`.
- **401 loop or silent stop.** Cause: the auth token expired. A non-200 response fails the connection permanently, so the app must notice `CLOSED` and rebuild the `EventSource`.

## Why

### Why it exists

HTTP is client-initiated. Without push, keeping data fresh means polling. With interval T, polling adds an average staleness of T/2. It also generates N/T requests per second for N clients whether or not anything changed. Long polling cuts the waste but still pays a full request round-trip per message, and every team reinvented its framing. SSE makes the push path cost proportional to events actually sent, at roughly one-way network latency, while staying inside HTTP's existing auth, routing, and caching rules.

### Why it looks the way it does

The design keeps the protocol small and standardizes only the parts that were being reinvented: framing, parsing, and reconnect.

- **The alternative was WebSocket's approach:** an Upgrade handshake, then a new binary framing layer with masking and its own ping/pong. That gains duplex and binary, but intermediaries must understand the Upgrade, and HTTP-level features (status codes, caching rules, HTTP/2 multiplexing) no longer apply. RFC 8441 later had to bolt WebSocket onto HTTP/2 separately. SSE's bet is that most push is one-way and the upstream direction is already solved by HTTP requests.
- **Text over binary.** Any server that can write and flush a string can emit SSE, and the stream can be read in a terminal.
- **Client-side reconnect with a server-supplied cursor.** The server stays stateless about *connections* and only needs to be stateful about *events*: a log indexed by ID. That splits responsibility the way Kafka consumer offsets do.
- **`GET`-only.** *Inference, not stated in the spec:* automatic reconnection means the browser re-sends the request without asking the app. That is only safe for an idempotent method, which would explain why the native API never grew `POST` support. `fetch`-based clients add `POST` and accept that risk.
- **A counter-example worth noting.** MCP's 2026-07-28 revision removed its long-lived `GET` stream, protocol-level sessions, and `Last-Event-ID` resumability. It keeps SSE only as a per-request response stream. That shows the operational cost of resumable long-lived streams behind load balancers and on serverless platforms.

### Why it matters now

As of 2026, SSE is growing.

- **AI APIs.** It is the de facto streaming transport for LLM APIs and agent protocols, so most engineers now consume it even if they never served it.
- **HTTP/2 and HTTP/3.** Both are the default at major CDNs and browsers, which removes the old connection-pool objection for most deployments.
- **Platform support.** .NET 10 made SSE a first-class result type, signalling it as a mainstream backend primitive.

WebSocket remains the answer for duplex traffic, and WebTransport is the long-term candidate for multi-stream binary. Neither has displaced SSE for one-way text push.

## Open questions / things to verify in practice

- Does my exact proxy chain (CDN, then load balancer, then nginx or YARP, then Kestrel) deliver each event immediately? Measure with `curl -N` and timestamps at each hop.
- What reconnect delay and backoff does each target browser actually apply after repeated failures, and does a server-sent `retry:` override the backoff?
- What does `TypedResults.ServerSentEvents` do about heartbeats and flushing? Do I still need my own `:` comments to survive a 60 s idle timeout?
- How much replay history do I need to keep (time window or event count) to cover realistic reconnect gaps, and what do I send when `Last-Event-ID` is older than the log?
- How many concurrent SSE connections can one Kestrel instance hold before memory or file-descriptor limits bite, and how does that change between HTTP/1.1 and HTTP/2?
- How do I authenticate the stream: cookies with `withCredentials`, a short-lived token in the query string (which ends up in access logs), or a `fetch`-based client with an `Authorization` header?
