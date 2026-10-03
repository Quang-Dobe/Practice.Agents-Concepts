# Server-Sent Events

Server-Sent Events (SSE) is a web standard for pushing updates from a server to a client over plain HTTP. The client opens one ordinary request, the server replies with the content type text/event-stream, and then it simply never finishes the response. Instead it keeps writing small text events down the open connection whenever it has something new, and the browser's built-in EventSource client parses each one and fires a JavaScript event for it.

It matters because normal HTTP is ask-and-answer, and some data does not fit that shape: live scores, build logs, notification feeds, progress bars, and LLMs streaming their output token by token. SSE gives you server push without leaving HTTP, so proxies, cookies, and auth middleware mostly keep working. It is one-way and text-only, so it is the wrong tool for chat or games where both sides talk constantly (use WebSockets), for binary data, or for reaching users whose tab is closed (use Web Push).

Think of it as tuning a radio instead of making a phone call. A normal request is a phone call: you ask, they answer, you hang up, and you call again to check for news. With SSE you tune in once and the station keeps broadcasting while you listen. If the signal drops, the radio retunes itself. A good station numbers its bulletins with an id field, so on reconnect the browser sends a Last-Event-ID header saying "I last heard #42" and the server can send only what you missed.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/backend/server-sent-events/present/index.html
