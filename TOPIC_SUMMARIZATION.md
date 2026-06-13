# gRPC

gRPC is a way to call a function on another machine as if it were local. You write one `.proto` schema that names the methods and message shapes, run a compiler, and you get strongly typed client and server code in your language of choice. Under the hood, calls travel over HTTP/2 as compact binary Protocol Buffers, with first-class support for streaming.

It matters because, between internal services, REST over HTTP/1.1 with JSON starts to feel chatty, weakly typed, and slower than it needs to be. Engineers reach for gRPC when both ends of a connection are services they control — polyglot backends that need a single source of truth for the contract, low-latency microservice traffic where JSON parsing is measurable, or real-time features like live telemetry, chat, and server push where they want streaming without bolting on WebSockets. It is not the right tool for public third-party APIs or for browsers without a proxy hop, where REST and OpenAPI still win.

A useful picture: REST is sending postcards — every request restates the address and re-opens an envelope of human-readable JSON. gRPC is installing a direct phone line between two services. Both ends agree up front on which functions exist and what their arguments look like, then `client.GetUser(req)` looks like a local method call. Because the line is HTTP/2, the same connection can also carry server-pushed streams, client-pushed streams, or full bidirectional conversations without a WebSocket upgrade.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/backend/grpc/
