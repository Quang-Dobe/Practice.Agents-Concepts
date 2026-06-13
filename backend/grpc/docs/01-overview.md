# gRPC — Overview

> gRPC is a way to call a function on another machine as if it were local — defined once in a schema, transported over HTTP/2 in a compact binary format, with client and server code generated for you.

## The 30-second version

gRPC is Google's open-source framework for service-to-service communication. You write a single `.proto` file describing your service's methods and message shapes, run a compiler, and out pop strongly-typed client and server stubs in your language of choice. Under the hood, calls travel over HTTP/2 as binary Protocol Buffers — smaller and faster than JSON over HTTP/1.1, with built-in support for streaming. Engineers reach for it when REST starts to feel chatty, weakly typed, or too slow between internal services.

## The mental model

Think of REST as sending postcards. Each request is a self-contained note with a verb (`GET`, `POST`), an address (`/users/42`), and human-readable JSON inside. Every postcard re-states the destination and re-opens an envelope.

gRPC is more like installing a direct phone line between two services. You agree up front on exactly which functions exist and what their arguments look like — that's the `.proto` contract:

```proto
service UserService {
  rpc GetUser (GetUserRequest) returns (User);
  rpc WatchUserEvents (UserId) returns (stream UserEvent);
}
```

From the caller's side, `client.GetUser(req)` looks like a local method call. The generated stub handles serializing the arguments to binary, multiplexing the call over a persistent HTTP/2 connection, and deserializing the response. Because HTTP/2 supports many simultaneous streams on one connection, that "phone line" can also carry server-pushed streams, client-pushed streams, or full bidirectional conversations — without the overhead of WebSocket upgrades or long-poll hacks.

Two ideas do most of the work: **the schema is the source of truth** (no more "what fields does this endpoint actually return?"), and **the wire format is binary and streamable** (no more parsing megabytes of JSON to read three fields).

## What it is NOT

- Not REST. REST is resource-oriented over HTTP/1.1 with human-readable JSON; gRPC is method-oriented over HTTP/2 with binary Protobuf.
- Not GraphQL. GraphQL lets clients shape their own queries; gRPC locks the shape to the `.proto` contract.
- Not a message queue. gRPC is synchronous request/response (with streaming); Kafka or RabbitMQ are for asynchronous, durable, fan-out messaging.
- Not browser-native. Browsers can't speak raw gRPC — you need gRPC-Web and a proxy.

## When you would reach for it

- Internal microservice-to-microservice communication where both ends are services you control.
- Polyglot backends — one team in Go, another in C#, another in Python — that all need to agree on a contract.
- Low-latency, high-throughput APIs where JSON parsing and HTTP/1.1 overhead are measurable costs.
- Real-time features that need streaming: live telemetry, chat, progress updates, server push.
- Mobile clients where payload size and battery matter.

## When you would NOT reach for it

- Public APIs consumed by third-party developers — REST and OpenAPI are still the lingua franca.
- Browser apps without a proxy layer — gRPC-Web works but adds a hop.
- Simple CRUD where REST plus JSON is already easy to debug with `curl`.
- Teams without tooling discipline — generated code, breaking-change rules, and Protobuf versioning have a learning curve.

## Key vocabulary (just enough to keep reading)

- **RPC** — Remote Procedure Call; calling a function that runs on another machine.
- **Protocol Buffers (Protobuf)** — Google's compact binary serialization format and schema language.
- **`.proto` file** — The contract: defines services, methods, and message types.
- **Stub** — Generated client code that makes remote calls look local.
- **HTTP/2** — The transport; supports multiplexed streams over a single connection.
- **Unary RPC** — One request, one response. The default shape.
- **Streaming RPC** — Client-stream, server-stream, or bidirectional.
- **Channel** — A long-lived connection a client uses to issue many calls.
- **Deadline** — A per-call timeout that propagates across services.
- **gRPC-Web** — A variant that lets browsers talk to gRPC through a proxy.

## What's next

The next document answers What / Where / When / How / Why in detail.
