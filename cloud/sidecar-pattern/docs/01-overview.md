# Sidecar Pattern — Overview

> A sidecar is a helper container that runs alongside your main application in the same deployment unit, handling cross-cutting plumbing (logging, TLS, proxying, secrets) so your app code stays focused on its actual job.

## The 30-second version
The sidecar pattern attaches a secondary process to your application — same lifecycle, same network, same local filesystem — to take over chores that aren't your application's core responsibility. Instead of every microservice re-implementing log shipping, retry logic, or mTLS, you bolt on a small purpose-built container that does it. The application doesn't know the sidecar exists. The platform team can swap, upgrade, or standardize the sidecar across hundreds of services without touching application code. This is the backbone of modern service meshes (Istio, Linkerd) and is now a first-class concept in Kubernetes itself as of v1.33.

## The mental model
Picture a motorcycle with a sidecar. The motorcycle is your application — fast, focused, doing one thing. The sidecar is a little attached cabin that goes everywhere the motorcycle goes. It can't drive on its own. It shares the same fuel tank and the same destination. But it carries the stuff the rider doesn't want to deal with: the cargo, the passenger, the GPS unit, the radio.

In Kubernetes terms, the "motorcycle + sidecar" is a single Pod with two containers. They share an IP address, they share volumes, and they live and die together. Your app container talks to `localhost:port` and the sidecar handles the messy outside world — TLS handshakes, log forwarding to Splunk, fetching secrets from Vault, retrying failed downstream calls. If you've ever used Istio, every Pod gets an Envoy proxy injected as a sidecar, and that proxy is what actually speaks to the network. Your service just talks to localhost.

## What it is NOT
- Not a separate microservice. A microservice has its own lifecycle and can be scaled independently. A sidecar is glued to its parent.
- Not an init container. Init containers run once before the main container starts; sidecars run for the whole life of the pod.
- Not the ambassador pattern. Ambassadors specifically proxy outbound calls; sidecar is the umbrella term and covers more than just proxying.
- Not the adapter pattern. Adapters normalize the main container's output for external consumers; again, narrower than sidecar.

## When you would reach for it
- You want consistent mTLS, retries, and traffic shaping across many services without modifying any of them (service mesh proxies like Envoy).
- You want to ship logs or metrics to a backend without bloating the app image (Fluent Bit, Promtail, OpenTelemetry Collector).
- You need short-lived secrets pulled at runtime and rotated, without baking them into the image (Vault Agent, cloud secret managers).
- You want to add features to a legacy application you cannot or will not rewrite.

## When you would NOT reach for it
- Your workload is a single small service and you don't need the abstraction — a library import is simpler than another container.
- Resource overhead matters more than uniformity (each sidecar adds CPU, memory, and a network hop).
- The "cross-cutting" concern is actually domain logic. Don't smuggle business rules into a sidecar; they belong in the app.
- Serverless functions where you don't control the runtime — you literally cannot attach one.

## Key vocabulary (just enough to keep reading)
- **Pod**: Kubernetes' smallest deployable unit — a group of containers that share network and storage.
- **Service mesh**: A layer that handles service-to-service networking, usually built on sidecar proxies.
- **Envoy**: The most common proxy used as a service-mesh sidecar.
- **mTLS**: Mutual TLS — both sides of a connection authenticate with certificates.
- **Init container**: A container that runs to completion before the main container starts. Not a sidecar.
- **Native sidecar**: Kubernetes v1.33+ feature — an init container with `restartPolicy: Always` that the scheduler treats as a true sidecar.
- **Injection**: The mesh control plane automatically adding a sidecar to every Pod via an admission webhook.
- **Cross-cutting concern**: A capability (logging, auth, tracing) needed everywhere but owned by nobody in particular.

## What's next
The next document answers What / Where / When / How / Why in detail — including how Kubernetes native sidecars actually work under the hood, how Istio's auto-injection wires Envoy into every Pod, and the real performance tradeoffs of adding a proxy hop to every request.
