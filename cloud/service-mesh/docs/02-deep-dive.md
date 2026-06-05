# Service Mesh — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
A service mesh is a programmable L4/L7 networking layer for east-west traffic in a distributed system, implemented as a **data plane** of identity-aware proxies sitting in the request path and a **control plane** that configures those proxies out-of-band. The data plane terminates and re-originates connections so it can enforce mTLS, weighted routing, retries, timeouts, circuit breaking, rate limiting, and per-request telemetry without the application speaking the mesh's API. The control plane translates high-level intent (Kubernetes CRDs like `VirtualService`, `PeerAuthentication`, Gateway API `HTTPRoute`) into proxy configuration and ships it to the data plane over a streaming gRPC API.

### The core building blocks
- **Sidecar proxy** — historically an Envoy container co-located with the app in the same pod, sharing its network namespace. Intercepts inbound and outbound TCP via iptables `REDIRECT` rules installed by an `istio-init` init container.
- **Per-node proxy (ambient)** — in Istio Ambient (GA November 2024) the L4 proxy is **ztunnel**, a Rust process running once per node as a DaemonSet. L7 features are opt-in via a **waypoint proxy** (Envoy) deployed per namespace or per service account.
- **Control plane** — Istio's `istiod` is the consolidated component (Pilot + Citadel + Galley merged in 1.5+). It watches the Kubernetes API, computes proxy config, signs workload certificates, and serves xDS.
- **xDS** — the discovery API family Envoy speaks: LDS (Listeners), RDS (Routes), CDS (Clusters), EDS (Endpoints), SDS (Secrets), with ADS aggregating them onto one ordered gRPC stream. Standard xDS is "state-of-the-world"; delta xDS sends only diffs.
- **SPIFFE identity** — every workload gets a SPIFFE ID like `spiffe://cluster.local/ns/default/sa/orders`, baked into an X.509 SVID that the proxy presents during mTLS handshakes.
- **CRDs** — `VirtualService` and `DestinationRule` (Istio), `ServiceProfile` (Linkerd), or the upstream **Gateway API** with `HTTPRoute` / `GRPCRoute` (the direction the ecosystem is converging toward).

### How it relates to the broader landscape
A service mesh is one member of a family of **out-of-process networking layers**. Its siblings are API gateways (north-south, single edge concentrator, e.g. Kong, Envoy Gateway), pure L4 load balancers (no L7 awareness), and library-based equivalents like gRPC interceptors, Twitter's Finagle, or Spring Cloud — same features, but inside the app process. CNI plugins (Cilium, Calico) sit one layer below at L3/L4; Cilium has been climbing the stack with its eBPF-based mesh, blurring the line. The mesh is the option that's polyglot, transparent to the app, and centrally configurable, at the cost of being an extra distributed system you operate.

## Where

### Where it runs / lives in the stack
In Kubernetes:
- **Sidecar mode**: an Envoy container inside every application pod. iptables rules in the pod's network namespace (installed by the `istio-init` initContainer) redirect all outbound TCP to port `15001` (`virtualOutbound`) and all inbound TCP to port `15006` (`virtualInbound`). Envoy itself runs as UID `1337`, and iptables skips redirection for that UID to prevent loops.
- **Ambient mode**: no sidecar. ztunnel runs as a per-node DaemonSet in the `istio-system` namespace, picks up pod traffic via a CNI plugin, and tunnels it over **HBONE** (HTTP/2 CONNECT on port `15008` carrying mTLS). L7 features live in waypoint proxies deployed as normal Deployments per namespace.
- **Control plane**: `istiod` runs in `istio-system`, typically 2–3 replicas behind a headless service, watching all namespaces it's responsible for.

### Where you typically encounter it
- **Istio** — the de-facto reference, ships in Google Cloud Service Mesh, Azure AKS Istio add-on, Red Hat OpenShift Service Mesh.
- **Linkerd** — CNCF graduated, Buoyant's lightweight Rust-proxy (`linkerd2-proxy`) mesh.
- **Consul Connect** — HashiCorp's mesh, only one that's first-class on VMs, not just Kubernetes.
- **Cilium Service Mesh** — eBPF-first, can run sidecarless using kernel-level interception; sometimes paired with an Envoy DaemonSet for L7.
- **AWS App Mesh** (deprecated, EOL September 2026) and **Kuma** (Kong's mesh, also Envoy-based) round out the field.

### Ecosystem and tooling
- **For identity**: SPIFFE/SPIRE, cert-manager (with `istio-csr`), Vault PKI.
- **For observability**: Prometheus + Grafana for the mesh's own metrics, Jaeger / Tempo / Zipkin for distributed traces emitted by the proxies, Kiali for the mesh topology view.
- **For policy at L7**: Open Policy Agent via Envoy's `ext_authz` filter, Istio `AuthorizationPolicy` CRDs.
- **For the API surface**: the **Kubernetes Gateway API** (GA 1.0 October 2023, with `GAMMA` extensions for mesh) is becoming the cross-mesh way to express routing instead of vendor CRDs.

## When

### When the topic emerged and why
The term was coined around 2016 inside Buoyant, originally describing **Linkerd 1.x** (a JVM proxy modelled on Twitter's Finagle). The motivation was direct: post-2014 microservices teams had re-implemented retries, circuit breakers, and timeouts in every language SDK, with subtle bugs in each. **Envoy** (open-sourced by Lyft, May 2017) gave the industry a high-performance, configurable C++ proxy. Google + IBM + Lyft launched **Istio** later that year using Envoy as the data plane. The sidecar pattern made the polyglot story tractable; Kubernetes' pod abstraction made it cheap to deploy.

### When to use it in a project
Reach for a service mesh when:
- You run **10+ services across 2+ languages** and observability has become inconsistent (each team picks their own metrics library).
- **Zero-trust** is a compliance or security requirement — every internal call must be mTLS with rotating identities, and you cannot mandate a single application framework.
- You need **progressive delivery** features (header-based routing, weighted canaries, fault injection) without coupling them to deployment tooling.
- You run **multi-cluster** or hybrid (VM + Kubernetes) and need a unified service identity and routing layer.
- You already have a platform team capable of operating one more distributed system.

### When NOT to use it
Avoid it when:
- You run **fewer than ~8 services**. The control plane is more moving parts than the workload.
- **Latency is the product** (HFT, real-time bidding, sub-millisecond gaming) — even Linkerd's ~0.8 ms p99 sidecar overhead is too much; library-based or kernel-bypass approaches win.
- Your team is **monolingual on a managed framework** (Spring Cloud, .NET Aspire) that already gives you mTLS, retries, and tracing.
- You have **no platform engineering capacity** — Istio's failure modes (config push storms, control-plane OOM, cert rotation stalls) require someone on call who understands xDS.

## How

### How it works under the hood
End-to-end lifecycle of a single request `orders -> payments` in sidecar mode:

1. **Pod startup**. The mutating admission webhook (`istio-sidecar-injector`) rewrites the pod spec at creation, adding an `istio-init` initContainer and an `istio-proxy` sidecar.
2. **Traffic capture**. `istio-init` runs `iptables -t nat` rules: outbound traffic in the pod's netns is redirected to port `15001`; inbound to `15006`; packets owned by UID `1337` (the Envoy user) bypass redirection.
3. **Config bootstrap**. The Envoy sidecar opens a long-lived gRPC stream to `istiod:15012` and subscribes via **ADS** (Aggregated Discovery Service). It receives, in order: CDS (clusters), then EDS (endpoints for those clusters), then LDS (listeners), then RDS (route configs attached to the listeners), then SDS (TLS certs). This ordering is mandated by the xDS spec — listeners must not reference routes that haven't arrived.
4. **mTLS material**. The proxy uses its Kubernetes service-account JWT to request an X.509 SVID from istiod's CA. The cert encodes the SPIFFE ID `spiffe://cluster.local/ns/orders/sa/orders-sa`. Default lifetime is **24 hours**; the proxy renews when 80% has elapsed (~19 hours). SDS pushes the new cert in-memory — no proxy restart.
5. **Request path**. The app calls `http://payments:8080`. The kernel routes the SYN to local port `15001`. Envoy looks up the original destination via `SO_ORIGINAL_DST`, matches the cluster `payments.default.svc.cluster.local`, picks an endpoint via the cluster's load-balancing policy (round-robin by default), opens an mTLS connection to the destination pod's port `15006`, presenting its SVID.
6. **Destination side**. The peer's inbound listener on `15006` terminates mTLS, validates the SPIFFE ID against any `AuthorizationPolicy`, and forwards plaintext over loopback to the app on its real port.
7. **Telemetry**. Both sides emit Prometheus metrics (`istio_requests_total`, `istio_request_duration_milliseconds`) and optional trace spans propagating B3 or W3C `traceparent` headers.

Ambient mode swaps step 2 for a CNI redirect into ztunnel on the node, and step 5 for an HBONE (HTTP/2 CONNECT) tunnel between node-local ztunnels; if a waypoint is configured, traffic detours through it for L7 policy.

### Traffic-shaping example
A 90/10 canary on the `payments` service, with retries and outlier detection:

```yaml
apiVersion: networking.istio.io/v1beta1
kind: DestinationRule
metadata:
  name: payments
spec:
  host: payments
  subsets:
    - name: v1
      labels: { version: v1 }
    - name: v2
      labels: { version: v2 }
  trafficPolicy:
    outlierDetection:
      consecutive5xxErrors: 5
      interval: 30s
      baseEjectionTime: 30s
      maxEjectionPercent: 50
---
apiVersion: networking.istio.io/v1beta1
kind: VirtualService
metadata:
  name: payments
spec:
  hosts: [payments]
  http:
    - route:
        - destination: { host: payments, subset: v1 }
          weight: 90
        - destination: { host: payments, subset: v2 }
          weight: 10
      retries:
        attempts: 3
        perTryTimeout: 2s
        retryOn: 5xx,reset,connect-failure
```

`istiod` translates this into a CDS update (two clusters with the subset endpoint filters) plus an RDS update (weighted route action plus a retry policy), pushes both over the existing ADS stream, and Envoy hot-applies them — no restart, no dropped connections.

### Key trade-offs

| Choice | Gained | Given up |
|---|---|---|
| Sidecar per pod | Strong isolation, per-pod identity, no node-shared blast radius | ~50–150 MB RAM and 0.1 vCPU per pod; ~1 ms p50 latency per hop |
| Ambient / per-node | Lower aggregate footprint, no app pod restarts on mesh upgrade | Node-level blast radius, weaker tenant isolation, newer code paths |
| Envoy data plane (Istio) | Richest feature set: WASM filters, ext_authz, every L7 protocol | Higher memory and CPU than Rust proxies; steeper config surface |
| Rust micro-proxy (Linkerd) | ~0.8 ms p99 overhead, ~1.2% network overhead in 100-service benchmarks | Smaller feature set, no WASM, fewer protocols |
| Vendor CRDs (Istio API) | Mature, very expressive | Lock-in; Gateway API is the portable successor |
| Library / Finagle-style | Zero hop, in-process — lowest latency | Polyglot pain; upgrading the library across 40 repos |

### Common failure modes
- **istiod CPU saturation** — every Service or Endpoint churn triggers a fresh xDS push to every connected proxy. In a 5k-pod cluster with rapid scale events, istiod CPU spikes and config propagation delays climb from sub-second to tens of seconds.
- **istiod OOM** — config size grows with `services × namespaces`; without `Sidecar` resources scoping each proxy's visibility, every sidecar gets every cluster's config.
- **Cold-start cert failure** — a sidecar starts before istiod is ready (or before its CA cache warms) and fails its SDS bootstrap; the app pod is up but every outbound call returns connection refused on `15001`.
- **Init container ordering bugs** — app initContainers that need network egress run before `istio-init` finishes, so their traffic isn't redirected and they hit raw cluster DNS or fail.
- **Retry storms** — a `retries.attempts: 3` policy on every hop in a 4-deep call chain multiplies into 81 attempts under a downstream brownout, amplifying the outage.
- **mTLS strict-mode rollouts** — flipping `PeerAuthentication` to `STRICT` while one workload is still un-meshed silently kills its traffic.

## Why

### Why it exists
At its root, a service mesh exists to enforce the **single-responsibility principle at the network layer**. Microservices fragmented a monolith's shared concerns — auth, retries, observability — into N copies that drift. A mesh re-centralizes them as a platform capability, the same way Kubernetes re-centralized process scheduling. It also lets security teams enforce zero-trust at L7 without depending on every application team to ship the right SDK upgrade.

### Why it looks the way it does
The sidecar pattern looks weird at first — why not put the proxy in the app process? Three reasons:
1. **Polyglot**. A C++ Envoy linked into a Python and a Go service is two FFI nightmares; a sidecar is a binary anyone can run.
2. **Lifecycle decoupling**. The proxy can be upgraded without re-deploying the app, and the app can crash without taking the proxy's cert cache with it.
3. **Identity boundary**. The sidecar has its own SPIFFE identity tied to the pod's service account; in-process, identity would be conflated with the app's threads.

The xDS / control-plane split mirrors the SDN (software-defined networking) heritage — dumb-fast forwarders in the data path, smart-slow controllers managing state. It's the same architecture as OpenFlow or BGP-routed fabrics.

The current shift to **ambient mode** is the industry conceding that one Envoy per pod is too expensive at scale (~150 MB × thousands of pods = real money), and that L4 security and L7 policy have different deployment cadences and can be split.

### Why it matters now
Three forces in 2026 keep service mesh relevant:
- **Zero-trust mandates** (Executive Order 14028, NIS2 in the EU) have made mTLS-everywhere a compliance line item, and a mesh is the cheapest way to deliver it.
- **The Kubernetes Gateway API GAMMA** initiative is finally standardizing mesh routing, eroding vendor lock-in and lowering the cost of switching between Istio, Linkerd, and Cilium.
- **AI workloads** (model-serving fleets, RAG pipelines) are reviving interest in cheap mTLS and traffic mirroring for evaluation; ambient mode's lower per-pod cost makes it usable for fleets of GPU pods where every gigabyte matters.

At the same time, the field is consolidating: AWS App Mesh is being deprecated (EOL September 2026), and the Envoy + Istio + Gateway API stack is becoming the default assumption. Worth knowing well; not worth deploying without justification.

## Open questions / things to verify in practice
- What's the actual p99 latency overhead of an Envoy sidecar on *my* workload? Benchmark with and without injection on the hot path.
- Does ambient mode hold under a noisy-neighbor pod hammering ztunnel? Test with a synthetic load generator on a shared node.
- How does istiod CPU scale when I scale my deployment replicas 10x in 30 seconds? Watch for xDS push latency.
- Are my sidecars actually scoped via `Sidecar` resources, or are they getting every cluster's config?
- Do my retry budgets compose safely across a 4-deep call chain, or am I one downstream blip away from a retry storm?
- After a Kubernetes node drain, how long until every proxy has the new endpoint set? (EDS propagation is the usual answer to "why was there a 30-second outage during deploy.")
