# Service Mesh — Overview

> A service mesh is a dedicated networking layer that handles service-to-service communication for you, so every microservice gets retries, mTLS, and observability without changing a line of app code.

## The 30-second version
Once you split a monolith into 40 microservices, every team starts re-implementing the same plumbing: TLS, retries, timeouts, circuit breakers, request tracing, "is service B even up?" A service mesh extracts that plumbing out of the apps and pushes it down into the infrastructure. Your `OrderService` keeps making a plain HTTP call to `http://payments`; the mesh intercepts that call and quietly adds encryption, load balancing, metrics, and a retry on 503. The payoff is that policy and observability become a platform feature, not a library that has to be upgraded in 40 repos.

## The mental model
Picture a city of microservices. Each service is a building. Without a mesh, every building has to hire its own security guard, postal worker, translator, and accountant — and they all do the job slightly differently. With a mesh, the city installs a **standardized concierge** right outside every building's front door. Your app talks only to its concierge in plain language ("send this to payments"); the concierges talk to each other in encrypted, logged, retry-capable conversations. The concierges are the **data plane** (the sidecar proxies, usually Envoy). They sit in the request path.

Above them sits **City Hall** — the **control plane** (Istio's `istiod`, Linkerd's controller). City Hall doesn't carry packets. It hands every concierge their rulebook: "route 10% of traffic to v2," "reject calls without a valid identity," "here's your fresh TLS cert." Change a rule in City Hall and every concierge updates within seconds. That split — dumb-but-fast concierges in the request path, smart-but-out-of-band rulemaker on the side — is the architectural heart of every mesh.

The classic deployment is the **sidecar**: in Kubernetes, your pod runs two containers, your app and an Envoy proxy. The proxy steals all inbound and outbound traffic via iptables. Your app thinks it's calling localhost. Note: the industry is shifting toward **sidecarless / ambient** modes (Istio Ambient, Cilium) where the proxy lives once per node instead of once per pod — same mental model, less overhead. We will fix that simplification in the deep dive.

## What it is NOT
- Not an **API gateway**. A gateway handles north-south traffic (client → cluster); a mesh handles east-west (service → service).
- Not a **load balancer**. It does load balance, but that's one feature of many; a pure LB has no identity, policy, or tracing layer.
- Not a **framework or library**. The whole point is that it lives outside your code — polyglot teams get the same features for free.
- Not **Kubernetes itself**. K8s gives you basic service discovery; a mesh sits on top and adds the smart networking.

## When you would reach for it
- You have 10+ microservices and observability is a mess of inconsistent logs.
- You need zero-trust networking — mutual TLS between every service, with rotating certs.
- You want progressive delivery: canary releases, traffic shifting by header, fault injection in staging.
- Compliance demands that "every internal call is encrypted and audited," and you cannot patch 40 services to do it.

## When you would NOT reach for it
- You have 3 services. The mesh is more moving parts than the system it manages.
- Latency is sacred (HFT, real-time gaming) — every hop through a proxy costs microseconds.
- Your team has no Kubernetes or platform engineer to own the control plane. Istio's failure modes are not friendly to part-timers.
- You already get most of these features from a single language framework (e.g., Spring Cloud) and you are not polyglot.

## Key vocabulary (just enough to keep reading)
- **Data plane** — the proxies in the request path.
- **Control plane** — the brain that configures the proxies.
- **Sidecar** — a proxy container running next to your app in the same pod.
- **Envoy** — the C++ proxy most meshes use as their data plane.
- **mTLS** — mutual TLS; both sides prove identity with certs.
- **Ambient mode** — sidecarless mesh; proxies run per-node, not per-pod.
- **Istio / Linkerd / Cilium** — the three meshes you will hear about most.
- **Waypoint proxy** — an optional L7 proxy used in ambient mode for richer policy.
- **East-west traffic** — service-to-service, the mesh's home turf.
- **Ingress / Egress gateway** — mesh-managed edge points for traffic entering or leaving the cluster.

## What's next
The next document (`02-deep-dive.md`) answers What / Where / When / How / Why in detail — including how Envoy intercepts traffic, how the control plane distributes config via xDS, and how sidecar vs ambient changes the trade-offs.
