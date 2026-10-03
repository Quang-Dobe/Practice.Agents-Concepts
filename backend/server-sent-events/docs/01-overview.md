# Server-Sent Events — Overview

> Server-Sent Events (SSE) is a web standard where the client opens one ordinary HTTP request and the server keeps the response open, pushing text events down it whenever it has something new.

## The 30-second version

Plain HTTP is ask-and-answer: the client asks, the server answers once, the connection is done. Some data doesn't fit that shape: live scores, build logs, notifications, an LLM generating tokens one at a time. SSE fixes this with the smallest possible change: the server answers with `Content-Type: text/event-stream` and then just doesn't finish the response. Browsers ship a built-in client, `EventSource`, which parses the stream, fires a JavaScript event per message, and reconnects on its own if the connection drops. You get server push without leaving HTTP.

## The mental model

Think of a **radio station versus a phone call**.

A normal HTTP request is a phone call to a help desk: you ask a question, they answer, you hang up. If you want to know whether anything changed, you call again (that's polling).

SSE is tuning a radio. You pick a station once (open the request), and from then on the station broadcasts and you listen. You can't talk back through the radio. If you want to say something, you use a different channel, such as a normal `POST`. If the signal cuts out, the radio retunes itself to the same station. A good station also numbers its bulletins ("this was bulletin #42"), so when you come back you can say "I last heard #42" and get only what you missed.

On the wire, each broadcast is a few plain-text lines followed by a blank line:

```text
id: 42
event: price-update
data: {"symbol":"ACME","price":101.5}

```

The browser side is a few lines:

```javascript
const source = new EventSource("/stream");
source.addEventListener("price-update", (e) => render(JSON.parse(e.data)));
```

Because it's still HTTP, proxies, load balancers, cookies, and auth middleware mostly work as usual. The main exception is response buffering, which the deep dive covers.

## What it is NOT

- Not WebSockets. WebSockets are a separate two-way protocol, carry binary frames, and start with a connection upgrade; SSE is one-way, text-only, plain HTTP.
- Not long polling. Long polling holds a request until one message arrives, then the client asks again; SSE keeps one response open for many messages.
- Not Web Push. Web Push wakes a service worker through the browser vendor's push service even when your page is closed; SSE only works while the page holds the connection.
- Not a message queue. SSE has no persistence, acks, or delivery guarantees on its own; replay after a reconnect only works if your server stores events and implements it.
## When you would reach for it

- Streaming LLM output token by token. Most major model APIs stream their responses this way.
- Live dashboards, tickers, and progress bars where the server talks and the client watches.
- Notification feeds, activity streams, and live CI build logs.
- Any push feature where you'd rather not run WebSocket infrastructure and the client→server direction is fine as normal HTTP requests.

## When you would NOT reach for it

- Chat, multiplayer games, collaborative editing: frequent messages in both directions point to WebSockets (or WebTransport).
- Binary payloads such as audio or video frames. SSE is UTF-8 text, so binary has to be base64-encoded.
- Notifying users whose tab is closed. Use Web Push.
- Serving many SSE streams per tab over HTTP/1.1. Browsers allow about 6 connections per origin, and each open stream holds one. Serve over HTTP/2 or HTTP/3.
- When you need custom request headers from the browser. The native `EventSource` only does `GET` and can't set headers like `Authorization`; you'd use cookies or a `fetch`-based client instead.

## Key vocabulary (just enough to keep reading)

- **`text/event-stream`**: the response content type that marks a response as an SSE stream.
- **`EventSource`**: the browser's built-in SSE client.
- **Event**: one message, made of field lines and terminated by a blank line.
- **`data:`**: the payload field. Several `data:` lines in one event are joined with newlines.
- **`event:`**: an optional event name, so clients can listen per type instead of to everything.
- **`id:`**: an optional event ID. The browser remembers the last one it received.
- **`Last-Event-ID`**: the request header the browser sends on reconnect so the server can resume.
- **`retry:`**: the server's instruction for how many milliseconds to wait before reconnecting.
- **Comment / heartbeat**: a line starting with `:`, which clients ignore. Servers send them now and then so idle proxies don't kill the connection.

## What's next

The next document answers What / Where / When / How / Why in detail: the exact wire format and parsing rules, the reconnect-and-resume lifecycle, how proxies and buffering break streams, how SSE compares to WebSockets and long polling under load, and how to serve it from .NET.
