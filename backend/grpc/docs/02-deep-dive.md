# gRPC — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

gRPC is an open-source RPC framework that defines services and messages in Protocol Buffers (proto3 / editions), serializes them with the Protobuf binary wire format, and transports them as length-prefixed messages inside HTTP/2 `DATA` frames over a single multiplexed TCP (or QUIC, in experimental builds) connection. Call status is delivered out-of-band as HTTP/2 trailers (`grpc-status`, `grpc-message`). The framework specifies four call shapes — unary, server-streaming, client-streaming, bidirectional-streaming — all expressed as a single HTTP/2 stream per RPC.

### The core building blocks

- **Protocol Buffers (proto3 / editions)** — the IDL and binary serialization format. Source of truth for messages, services, and field numbering.
- **`protoc` + language plugins** — the compiler that turns `.proto` files into generated stubs (client) and skeletons/base services (server) in 11+ languages.
- **HTTP/2 transport** — provides streams, frame multiplexing, header compression (HPACK), and trailers. Specified in [PROTOCOL-HTTP2.md](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md).
- **Length-prefixed message framing** — each Protobuf message inside a `DATA` frame is prefixed with a 5-byte header: 1 byte for the compression flag, 4 bytes for the big-endian message length.
- **Channel** — a long-lived, load-balanced abstraction over one or more HTTP/2 connections. Client stubs are stateless wrappers around a channel.
- **Metadata** — key-value pairs carried in HTTP/2 `HEADERS` frames; the equivalent of HTTP headers. Binary metadata keys must end in `-bin` and are base64-encoded.
- **Status model** — 17 canonical codes (0–16) carried as the `grpc-status` trailer, with optional `grpc-message` and rich `google.rpc.Status` details.
- **Interceptors** — middleware that wrap unary or streaming calls on either client or server side for cross-cutting concerns (auth, logging, tracing).

### How it relates to the broader landscape

gRPC sits in the **schema-first RPC** family alongside Apache Thrift, Cap'n Proto, and (historically) CORBA and DCE/RPC. It is *not* in the same family as REST/OpenAPI (resource-oriented HTTP) or GraphQL (single-endpoint query language). Compared to Thrift, gRPC standardizes on one transport (HTTP/2) and one serialization (Protobuf) instead of Thrift's pluggable matrix — less flexibility, more interoperability. Compared to GraphQL, gRPC is method-oriented and fixed-shape; the client cannot ask for "only these fields."

## Where

### Where it runs / lives in the stack

Application layer (L7). It replaces or augments the JSON-over-HTTP layer between services. It does not replace the message bus (Kafka, NATS), the service mesh (Envoy, Linkerd — which often *carries* gRPC), or the database protocol. In a service mesh, gRPC is the application protocol the mesh sidecars proxy.

### Where you typically encounter it

- **Kubernetes internals** — the `kubelet` ↔ container runtime CRI is gRPC; CSI and CNI plugins use gRPC.
- **etcd** — its client protocol is gRPC.
- **Envoy xDS** — control-plane configuration streamed over gRPC.
- **TensorFlow Serving / NVIDIA Triton** — model-inference RPCs.
- **Dapr** — sidecar-to-app communication.
- **Google Cloud APIs and many AWS internal services** — gRPC is the wire-level protocol; REST is a generated facade.

### Ecosystem and tooling

- **For schema management:** `protoc`, `buf` (lint, breaking-change detection, registry).
- **For runtime in .NET:** `Grpc.AspNetCore` (server), `Grpc.Net.Client` (client) built on `Kestrel` and `HttpClient`.
- **For runtime elsewhere:** `grpc-go`, `grpc-java`, `grpc-python`, `grpc/grpc` (the C-core used by C++, Ruby, PHP, Python, Objective-C).
- **For browsers:** gRPC-Web + Envoy (or `grpc-web` proxy); Connect-RPC as a wire-compatible alternative.
- **For debugging:** `grpcurl`, `grpcui`, Postman, Kreya, BloomRPC — all use **server reflection**.
- **For REST bridging:** `grpc-gateway` (Go), transcoding in Envoy, ASP.NET's `Grpc.AspNetCore.Server.Reflection` + JSON transcoding.

## When

### When the topic emerged and why

Google open-sourced gRPC in 2015 as the public face of **Stubby**, its internal RPC system that had been used for over a decade. The trigger was the standardization of HTTP/2 (RFC 7540, May 2015), which finally gave the open ecosystem a transport with multiplexing, binary framing, and header compression — the things Stubby relied on. Before gRPC, the choices for typed cross-service calls were Thrift (Facebook, 2007), Avro RPC (Hadoop ecosystem), or hand-rolled JSON-over-HTTP. None had Google's tooling polish or the HTTP/2 timing.

### When to use it in a project

Reach for it when:
- Both ends are services you own and can regenerate clients for.
- You have a polyglot backend that needs a single source of truth for contracts.
- Latency or payload size is a measurable cost (mobile, high-QPS internal APIs, large message volumes).
- You need streaming (server push, telemetry, live updates) without WebSocket plumbing.
- Deadline and cancellation propagation across a call graph matter (request hedging, tail-latency control).

### When NOT to use it

Avoid it when:
- The API is consumed by unknown third parties — REST + OpenAPI is still the lingua franca.
- The primary client is a browser and you cannot run a proxy.
- The team has no tooling discipline for generated code and breaking-change review.
- The workload is fire-and-forget event distribution — that's a message bus problem.

## How

### How it works under the hood

A unary RPC, end to end:

1. **Compile time.** `protoc` reads `service Foo { rpc Bar (Req) returns (Resp); }` and emits a stub class `FooClient` with a `Bar(Req, CallOptions)` method and a server base class `FooBase` with a `virtual Bar(Req, ServerCallContext)` to override.
2. **Channel setup.** The client opens (or reuses) an HTTP/2 connection. TLS is negotiated via ALPN with the `h2` token; cleartext (`h2c`) is allowed for trusted networks.
3. **Stream open.** A new HTTP/2 stream (odd-numbered, client-initiated) is allocated for the call. The path is `/<package>.<Service>/<Method>`.
4. **Request HEADERS.** The client sends `:method POST`, `:path /foo.FooService/Bar`, `content-type: application/grpc+proto`, `te: trailers`, `grpc-timeout: 1500m` (1500 ms), plus user metadata.
5. **Request DATA.** The Protobuf-encoded message is prefixed with `[compressed-flag:1B][length:4B big-endian][payload]` and sent in one or more `DATA` frames. `END_STREAM` is set on the last frame.
6. **Server dispatch.** The server framework reads the prefix, allocates a buffer of the announced length, decodes the Protobuf message, and invokes the generated handler.
7. **Response HEADERS.** Status `200 OK` plus `content-type`.
8. **Response DATA.** Same length-prefixed framing, in `DATA` frames.
9. **Response TRAILERS.** A second `HEADERS` frame carries `grpc-status: 0` and optionally `grpc-message: ...` and `grpc-status-details-bin: ...` (base64-encoded `google.rpc.Status`). `END_STREAM` is set.

The four call shapes differ only in which side keeps the stream open:

| Shape | Client DATA frames | Server DATA frames |
|-------|--------------------|--------------------|
| Unary | 1 | 1 |
| Server-stream | 1 | N (until server closes) |
| Client-stream | N (until client closes) | 1 |
| Bidi-stream | N | N (interleaved) |

**Why the Protobuf wire format is compact.** Every field is encoded as `tag = (field_number << 3) | wire_type`, varint-encoded. Wire types: `0` varint, `1` 64-bit fixed, `2` length-delimited, `5` 32-bit fixed (types 3 and 4 — start/end group — are deprecated). Varints encode integers in 7-bit groups with a continuation bit; small integers fit in one byte. `sint32`/`sint64` use **zig-zag** pre-encoding (`(n << 1) ^ (n >> 31)` for 32-bit) so that small negatives stay small — otherwise `-1` would always be 10 bytes. Unset proto3 fields with default values are *not transmitted* at all; the receiver fills in defaults. There are no field names on the wire, only numbers — which is why renaming a field is free and changing its number is a breaking change.

Concretely, the message `Person { string name = 1; int32 age = 2; }` with `name="Al", age=30` is 7 bytes: `0a 02 41 6c 10 1e` — tag 1/length-delimited, length 2, "Al", tag 2/varint, 30.

**Deadlines** travel as the `grpc-timeout` header (e.g. `1500m`, `2S`, `5M`). They are absolute from the caller's perspective: when an inbound RPC is received, frameworks compute the remaining time and use it as the default deadline for any outbound RPC made from inside that handler. A downstream service can shorten the deadline; it cannot extend it. When the deadline elapses, both sides observe a `DEADLINE_EXCEEDED` (status code 4) and any in-flight work tied to the context is cancelled.

**Metadata** is just HTTP/2 headers in two buckets: initial metadata (sent with the first HEADERS frame) and trailing metadata (sent with the trailers). String values go through as-is, subject to HPACK compression; binary values must use a `-bin` key suffix and be base64 in transit. Authentication tokens, trace contexts (`traceparent`), and tenancy IDs are the usual passengers.

**Interceptors** are typed middleware. The signature is roughly "given the inbound call and a `next` continuation, do work before, call `next`, do work after." Unary and streaming interceptors are separate types in most implementations because the streaming form must observe each message individually. They compose into a chain and run on every RPC.

**Status and the error model.** The canonical codes are 0 `OK`, 1 `CANCELLED`, 2 `UNKNOWN`, 3 `INVALID_ARGUMENT`, 4 `DEADLINE_EXCEEDED`, 5 `NOT_FOUND`, 6 `ALREADY_EXISTS`, 7 `PERMISSION_DENIED`, 8 `RESOURCE_EXHAUSTED`, 9 `FAILED_PRECONDITION`, 10 `ABORTED`, 11 `OUT_OF_RANGE`, 12 `UNIMPLEMENTED`, 13 `INTERNAL`, 14 `UNAVAILABLE`, 15 `DATA_LOSS`, 16 `UNAUTHENTICATED`. The "rich" error model packs a `google.rpc.Status { code, message, repeated Any details }` into `grpc-status-details-bin`. Standard detail types live in `google/rpc/error_details.proto`: `BadRequest`, `RetryInfo`, `QuotaFailure`, `PreconditionFailure`, `ErrorInfo`, `DebugInfo`, `Help`, `LocalizedMessage`, etc.

**Server reflection** (`grpc.reflection.v1.ServerReflection`) is itself a gRPC service. It exposes a bidi-streaming method `ServerReflectionInfo` that lets a client ask "what services do you expose?", "give me the `FileDescriptorProto` for this symbol", and so on. This is how `grpcurl` can call a server with no `.proto` file on disk — it pulls descriptors over the wire and decodes binary responses to JSON on the fly.

### Key trade-offs

| Choice | Gained | Given up |
|--------|--------|----------|
| Protobuf binary over JSON | Smaller payloads (often 30–50%), faster parse, schema enforcement | Not human-readable; requires tooling to inspect |
| HTTP/2 single connection | Multiplexing, fewer TCP handshakes, header compression | TCP-level head-of-line blocking still applies; one bad connection stalls all streams |
| Code generation | Strong typing, IDE autocomplete, fewer integration bugs | Build-system complexity, generated diffs in PRs, language-plugin maturity varies |
| Field numbers as identity | Cheap field rename, forward/backward compatibility by construction | Accidental number reuse is a silent data corruption bug |
| Status as trailers | Status can reflect events that happen mid-response | Some HTTP/2 proxies and L7 load balancers handle trailers poorly |
| Streaming as first-class | No WebSocket upgrade dance; backpressure via flow control | Streams hold server resources; lifecycle is more complex than request/response |
| Method-oriented contract | Predictable, typed, refactor-friendly | Each new query shape needs a new RPC; clients cannot field-select like GraphQL |

### Common failure modes

- **Single channel saturation.** All streams share one TCP connection; if it hits the HTTP/2 `MAX_CONCURRENT_STREAMS` (default 100 in many servers) or saturates a single L4 hop, new RPCs queue. Fix: subchannels / multiple channels / client-side load balancing.
- **Load balancer mis-routing.** An L4 LB that pins a TCP connection to one backend will send 100% of a client's traffic to one pod. gRPC needs L7-aware LB or client-side LB.
- **Field number reuse.** Deleting a field and reusing its number in a later schema silently corrupts old data. Mitigation: `reserved 4;` in proto.
- **Missing `Trailer` handling at a proxy.** Older proxies or middleboxes that don't forward HTTP/2 trailers cause every call to appear hung or return `UNKNOWN`.
- **No deadline set.** A client without a deadline plus a slow server pins server resources indefinitely. Goroutines/threads pile up.
- **`UNAVAILABLE` on cold start.** TLS handshake plus name resolution race with the first RPC. Mitigation: explicit channel warmup or retry with backoff.
- **Browser unreachability.** Forgetting that browsers cannot speak native gRPC, then discovering it at integration time.

## Why

### Why it exists

The fundamental problem is **typed, low-overhead, cross-language service-to-service calls at scale**. In a microservice graph, every hop pays serialization, transport, and contract-drift costs. JSON over HTTP/1.1 amplifies all three: text parsing is CPU-bound, HTTP/1.1 forces connection-per-request or head-of-line-blocked pipelining, and the contract lives in tribal knowledge or OpenAPI files that drift from the server. gRPC collapses contract + transport + codegen into one pipeline.

### Why it looks the way it does

Why HTTP/2 and not raw TCP? Because the open internet already had to allow HTTP/2 through firewalls and proxies for the web — riding on top gets you transit for free. A bespoke TCP protocol would be blocked everywhere.

Why Protobuf and not JSON or MessagePack? Protobuf is schemaful by design — the wire format is meaningless without the schema, which forces teams to maintain it. JSON's self-describing nature is exactly the property that lets contracts rot. MessagePack is compact but schemaless.

Why field numbers instead of field names? Names cost bytes per message and per field, and renaming is a common refactor. Numbers decouple the wire identity from the source identity, which is the single most important property for long-lived service evolution.

Why status in trailers instead of the response code? Because for streaming responses, the server may discover an error halfway through producing the stream — after the `200 OK` headers are already on the wire. Trailers let the final outcome arrive after the body, which `HTTP/1.1` could not cleanly do.

### Why it matters now

In 2026, gRPC is mature and stable rather than novel. It is the default internal protocol for new Kubernetes-native infrastructure (CRI, CSI, CNI, xDS, OpenTelemetry collectors), most cloud-provider control planes, and AI/ML serving (TensorFlow Serving, Triton, Ray). The interesting movement is at the edges: **Connect-RPC** offers a wire-compatible alternative with better browser ergonomics; **gRPC over QUIC / HTTP/3** is being prototyped to address TCP head-of-line blocking; **buf** has largely replaced ad-hoc `protoc` workflows for schema governance. Net direction: deeper entrenchment in infra, broader reach into the browser via Connect and gRPC-Web.

## Open questions / things to verify in practice

- How does our chosen L7 load balancer (Envoy / NGINX / cloud LB) actually behave with long-lived HTTP/2 connections and `MAX_CONCURRENT_STREAMS`?
- What is the real payload-size and latency delta of Protobuf vs JSON on *our* message shapes, not a benchmark blog's?
- Does our deadline propagate end-to-end through every interceptor, or does some library swallow it?
- When the server returns a rich `google.rpc.Status` with `BadRequest` details, does our client surface those details to callers or flatten to a string?
- How does `grpc-go` (or `Grpc.Net.Client`) recover when the underlying HTTP/2 connection is killed mid-stream — automatic retry, application-visible error, or both?
- For a streaming RPC, how is backpressure actually exposed? Does our client block on `Send`, drop, or buffer unbounded?
