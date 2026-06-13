# gRPC — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical polyglot backend, gRPC is the wire between internal services: a Go ingest pipeline calling a C# pricing service, a Python ML scorer fronting a Java order service. Anywhere a team wrote "we'll just use JSON for now" and then watched the p99 climb, gRPC tends to show up on the next quarter's roadmap.

In Kubernetes-native infrastructure it is already there whether the team picked it or not — the kubelet talks CRI over gRPC, CSI/CNI plugins are gRPC servers, the Envoy control plane streams xDS over gRPC, OpenTelemetry collectors accept OTLP/gRPC. If a service runs on K8s and exposes a port that is not `:80` or `:443`, the odds it speaks gRPC are non-trivial.

In ML serving and platform tools (TensorFlow Serving, NVIDIA Triton, Temporal, etcd, Dapr) gRPC is the load-bearing API. Application teams meet it as a *client* before they ever stand up a server of their own.

In product code, the common pattern is gRPC internally + a thin REST/JSON facade (grpc-gateway, ASP.NET JSON transcoding, or a hand-written BFF) at the edge for browsers and partners.

## Best practices

### 1. Treat the `.proto` repo as a product, not a side artifact
**Do:** Store `.proto` files in a dedicated repo or top-level `/proto` tree, lint with `buf lint`, run `buf breaking` against the main branch in CI, and publish generated stubs as versioned packages (NuGet, npm, Go module).
**Why:** Schemas are the contract between teams that ship on different cadences. If proto changes land via copy-paste, you will get silent skew where the client and server disagree on field numbers — the single worst class of gRPC bug because it deserializes successfully but to the wrong field.
**Avoid:** Hand-copying `.proto` files into each service repo "for convenience."

### 2. Evolve schemas additively, and `reserved` everything you remove
**Do:** Add new fields with new numbers. When deleting a field, replace it with `reserved 7; reserved "old_field_name";`. Never change a field number or type. Treat oneof additions as breaking unless guarded.
**Why:** Protobuf identity is the field number. Reusing number 7 for a new meaning silently corrupts old clients' data — the bytes decode, just into the wrong slot. The bug surfaces as ghost values in dashboards weeks later.
**Avoid:** "We're the only client, it's fine to renumber." There is always one stale pod, one cached binary, one mobile app version you forgot.

### 3. Set a deadline on every outbound call, and propagate the inbound one
**Do:** Configure a per-call deadline in client code (`CallOptions.WithDeadline` / `context.WithTimeout`). In a server handler, derive outbound deadlines from `ServerCallContext.Deadline` so the budget shrinks down the call graph.
**Why:** Without a deadline, a slow downstream pins server threads/goroutines forever. One stuck dependency takes the whole fleet to its connection limit in minutes. Deadline propagation is what makes tail-latency control possible across hops.
**Avoid:** Relying on TCP timeouts or "the LB will kill it." Neither knows about your RPC semantics.

### 4. Pick a retry strategy explicitly, and only retry idempotent methods
**Do:** Configure retries via gRPC service config (`methodConfig.retryPolicy`) with explicit `retryableStatusCodes` — typically `UNAVAILABLE`, sometimes `RESOURCE_EXHAUSTED`. Use exponential backoff with jitter. Mark mutating methods non-retryable, or require an idempotency key in the request.
**Why:** Auto-retrying a `CreateOrder` on `UNAVAILABLE` after the server already committed but before the trailer arrived gives you duplicate orders. The retry token bucket (default `maxTokens=10`, `tokenRatio=0.1`) exists to prevent retry storms from amplifying an outage — disable it at your peril.
**Avoid:** A blanket `RetryPolicy(maxAttempts=5)` on every method. That is how brownouts become full outages.

### 5. Use client-side load balancing with a real resolver, not a K8s `ClusterIP`
**Do:** In Kubernetes, expose the gRPC server as a **headless service** (`clusterIP: None`), use the `dns://` resolver, and set `loadBalancingConfig: [{ round_robin: {} }]` on the channel. Or front the service with an L7-aware proxy (Envoy, Linkerd, Istio).
**Why:** A normal `ClusterIP` does L4 load balancing. gRPC opens one HTTP/2 connection and multiplexes thousands of streams over it — so 100% of one client's traffic lands on one pod. Adding more replicas does nothing. This is the single most common gRPC-on-K8s production surprise.
**Avoid:** Assuming "Kubernetes load balances for me." It does, just at the wrong layer for HTTP/2.

### 6. Use the canonical status codes, and the rich error model when you need details
**Do:** Map domain errors to the 16 canonical codes (`INVALID_ARGUMENT` for validation, `FAILED_PRECONDITION` for state-machine errors, `ABORTED` for concurrency conflicts, `UNAVAILABLE` only for "try again"). When you need structured detail, attach `google.rpc.BadRequest`, `RetryInfo`, or `ErrorInfo` via `google.rpc.Status` in `grpc-status-details-bin`.
**Why:** Retries, circuit breakers, and dashboards all key off the code. `INTERNAL` for everything destroys observability and makes safe retries impossible. A `BadRequest` detail with field paths is what lets a client render "email is invalid" instead of "request failed."
**Avoid:** Throwing raw exceptions and letting the framework map them to `UNKNOWN`. That is a code smell visible from orbit.

### 7. Always wrap calls in interceptors for auth, tracing, and metrics
**Do:** Install a chain: auth interceptor (validate JWT/mTLS identity, populate context), OpenTelemetry interceptor (extract `traceparent` from metadata, start span, record `rpc.grpc.status_code`), metrics interceptor (RED — rate, errors, duration — per method). Use the auto-instrumentation packages where available.
**Why:** Per-method observability is the difference between "one RPC is slow" and "we have no idea." Interceptors are the only place to add it once and have it cover unary + all three streaming shapes consistently.
**Avoid:** Logging inside each handler. You will forget half of them and double-log the rest.

### 8. Enforce TLS, prefer mTLS between services
**Do:** Terminate TLS at the gRPC server (or sidecar) with a real cert; use ALPN `h2`. For service-to-service, use mTLS — a service mesh (Linkerd, Istio) or SPIFFE/SPIRE issues short-lived identities. Disable `h2c` (cleartext) outside of localhost.
**Why:** gRPC metadata routinely carries bearer tokens and tenant IDs. Cleartext between pods is a compliance failure and a credential-leak vector. mTLS makes "who called me" a property of the connection, not a header you have to trust.
**Avoid:** A hand-rolled "we use a shared secret in metadata" scheme. That's reinventing auth, badly.

### 9. Disable server reflection in production
**Do:** Register the reflection service only when an env var (`ENABLE_GRPC_REFLECTION=true`) is set, or behind an internal-only port. Keep it on in dev so `grpcurl` / `grpcui` / Postman work without `.proto` files.
**Why:** Reflection lets anyone with network reach enumerate every service, method, and message shape — your full API surface, no auth required. It is exactly the recon step you do not want to make free.
**Avoid:** Shipping the same Helm chart from dev to prod with reflection always on.

### 10. Cap message size, and stream anything that could grow
**Do:** Keep the default 4 MiB inbound limit. If a payload could legitimately exceed ~1 MiB, change the RPC to a server- or client-streaming shape that emits chunks. For genuinely huge blobs, hand out a presigned URL to object storage instead of streaming bytes through gRPC.
**Why:** A single 200 MiB message blocks one HTTP/2 stream's flow-control window for the duration, holds a contiguous buffer in memory on both ends, and makes deadlines almost meaningless. Streaming gives you backpressure and incremental progress.
**Avoid:** Raising `MaxReceiveMessageSize` to 100 MiB to "just make it work." You're papering over a design issue.

### 11. Handle streaming backpressure and half-close explicitly
**Do:** On the server side of a server-stream, check `IsReady` / write-flow-control before each send. On a bidi stream, treat `CompleteAsync()` (client half-close) as a normal signal, not an error. Bound any in-memory queue you put between business logic and the stream writer.
**Why:** A fast producer + slow consumer with an unbounded channel is an OOM waiting to happen. Forgetting half-close semantics leads to streams that hang until the deadline fires — every time.
**Avoid:** `while(true) stream.WriteAsync(...)` with no flow control. Looks fine in tests, melts under load.

### 12. Keep one channel per target, not one per call
**Do:** Construct a `GrpcChannel` (or equivalent) once per target service, reuse it for the lifetime of the process, and let it manage subchannels. Configure keepalive (`GRPC_ARG_KEEPALIVE_TIME_MS` ~30–60s) so idle connections survive NAT/LB timeouts.
**Why:** Channel construction is expensive — TLS handshake, name resolution, HTTP/2 SETTINGS exchange. Creating one per call defeats every benefit of HTTP/2 and can quadruple p99 latency. No keepalive means cold-start `UNAVAILABLE` after idle periods.
**Avoid:** `using var channel = GrpcChannel.ForAddress(...)` inside a request handler.

## Anti-patterns to recognize

- **gRPC as a public API**: Exposing raw gRPC to third-party developers. Browsers can't call it natively, SDKs lag, and you lose the curl-able debuggability that REST gives free. Use REST/OpenAPI or grpc-gateway/Connect at the edge.
- **Renumbering a field "because it's cleaner"**: A rename of `customer_id` from field 3 to field 4. Compiles fine, passes tests, silently writes customer IDs into the wrong slot for any client still on the old schema. Always additive; always `reserved`.
- **Catch-all `INTERNAL` errors**: Every server exception becomes `Status.Internal("something went wrong")`. Clients can't tell retryable from permanent; dashboards become a flat line. Map to specific codes; use rich details for context.
- **L4 load balancer in front of HTTP/2**: A cloud `Service` of type `LoadBalancer` (NLB / ClusterIP) routing gRPC. One TCP connection means one backend pod gets everything. Use headless DNS + client-side LB, or an L7 mesh.
- **Reflection enabled in prod**: Convenient for `grpcurl`, but it hands attackers your schema. Toggle via env var; never default-on.
- **Deadlines set only at the edge**: The API gateway has a 30 s deadline, downstream services have none. The first hung dependency exhausts the call stack. Propagate inbound deadlines into every outbound call.
- **Streaming used as "long-lived RPC" with no keepalive**: A bidi stream that the load balancer silently drops after 60 s idle, and neither side notices for hours. Configure HTTP/2 PING keepalive and treat stream errors as recoverable.
- **One giant `.proto` file with every service**: Works at 3 services, agony at 30. Split by bounded context; version with `package foo.v1`; introduce `foo.v2` rather than mutating `v1`.

## Real-world usage patterns

**Internal mesh at a mid-size SaaS** (50–200 services, mixed Go/.NET/Python). gRPC is the default for sync service-to-service calls; Kafka handles events. Proto files live in a monorepo with `buf` for lint + breaking-change CI; generated stubs are published as language-specific packages. Lesson: the breaking-change check in CI is what makes the whole thing work — without it, "additive only" becomes a guideline nobody enforces.

**ML inference platform** (Triton or custom TF Serving fork). Clients send tensors as gRPC messages; the server streams batched results back. Max message size is bumped to ~32 MiB intentionally. Lesson: when you really do need big messages, isolate the inference service behind its own channel pool with its own retry policy — don't let it inherit the global one tuned for 1 KiB CRUD calls.

**Kubernetes-native control plane** (Envoy xDS, Linkerd, an internal operator). The control plane streams config updates over a long-lived bidi gRPC stream; data-plane sidecars consume them. Reconnect-on-stream-error is mandatory because pod churn kills streams constantly. Lesson: streaming gRPC at infra scale is mostly about *reconnect logic*, not the happy path.

**Mobile-first product with a BFF**. Native apps speak gRPC to a BFF; the BFF fans out to internal gRPC services. Deadline budget is 2 s at the BFF, shrinks to 1.2 s by the time it hits the slowest downstream. Lesson: a strict deadline budget per hop is the cheapest tail-latency control you will ever deploy.

**Browser SPA via Connect-RPC / gRPC-Web**. Frontend calls a Connect-RPC server that speaks both Connect (browser) and grpc+proto (internal). No Envoy hop needed. Lesson: if browser support is a hard requirement and you're starting fresh, Connect avoids the proxy tax — but you're committing to its ecosystem maturity, which is smaller than mainline gRPC's.

## Operational checklist

- **Metrics:** per-method RPS, error rate by `grpc-status` code, p50/p95/p99 latency, in-flight stream count, channel state changes. Are they exported by interceptor, not hand-rolled per handler?
- **Tracing:** does `traceparent` propagate via metadata across every hop, including streams?
- **Deadlines:** is there a default deadline at the channel level, and does it propagate through server handlers to outbound calls?
- **Retries:** which methods are marked retryable? Is the policy in service config, not buried in client code? Are mutating RPCs explicitly excluded?
- **Schema CI:** does `buf breaking` run on every PR against the main branch? Does it block merge?
- **Load balancing:** is the K8s service `clusterIP: None` (headless), or fronted by an L7 mesh? Has someone verified traffic spread under load, not just by reading config?
- **TLS:** is `h2c` disabled outside localhost? Are certs rotated? If mTLS, who issues identities?
- **Reflection:** is it gated behind an env var or internal-only listener in production?
- **Message size:** what's the configured max? What's the p99 actual size? Is anything close to the limit a candidate for streaming?
- **Onboarding:** can a new engineer call a service from `grpcurl` in dev in under 5 minutes? Do they know which port is gRPC vs HTTP, and which interceptors run by default?

## How this topic typically evolves in a codebase

Teams usually start with one `.proto` file in the same repo as their first service, hand-copied into the client. There's no `buf`, no breaking-change CI, deadlines are optional, and the K8s service is a plain `ClusterIP`. It works because there are two services and four engineers.

The first painful migration is around the 10-service mark: the proto file has fanned out into copies that no longer match, a renamed field has caused a quiet bug in production, and someone has noticed that scaling the server pod count doesn't actually spread the load. The fix is a proto monorepo with `buf`, generated package publication, and either a service mesh or headless services with client-side LB. This migration takes a quarter and is universally underestimated.

The mature shape, often a year or two later, is: proto registry (Buf Schema Registry or homegrown), generated stubs as versioned packages, mesh-issued mTLS, deadlines and retries codified in service config, OpenTelemetry interceptors as a shared internal library, and a clear `v1`/`v2` versioning convention for breaking-change-by-design. The hard problem stops being "does gRPC work" and becomes "how do we deprecate `v1` without paging the on-call."

## Further reading

- [gRPC official docs — guides section](https://grpc.io/docs/guides/) — the canonical source for retries, deadlines, load balancing, OpenTelemetry metrics. Skim the whole section once.
- [grpc/proposal — gRFCs on GitHub](https://github.com/grpc/proposal) — the design docs (A6 retries, A8 health checking, A27 xDS). Worth reading the ones for features you depend on.
- ["Lessons learned from running a large gRPC mesh at Datadog"](https://www.datadoghq.com/blog/grpc-at-datadog/) — concrete production stories on connection management, load balancing, and observability at scale.
- [Buf docs — schema management and breaking-change detection](https://buf.build/docs) — the de facto modern toolchain. Replace ad-hoc `protoc` workflows with this.
- [Microsoft Learn — gRPC for .NET in production](https://learn.microsoft.com/en-us/aspnet/core/grpc/) — versioning, retries, performance, and JSON transcoding from the ASP.NET perspective; pragmatic and current.
- ["Breaking gRPC" by kmcd.dev](https://kmcd.dev/posts/breaking-grpc/) — a focused walkthrough of field-number compatibility and what actually breaks on the wire. Short and clarifying.
