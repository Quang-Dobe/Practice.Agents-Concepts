# Service Mesh — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical mid-size SaaS, the mesh shows up the moment the platform team is asked to "make every internal call mTLS by Q3" or "tell me why checkout p99 doubled last Tuesday." It sits between your Kubernetes Services and your apps' Envoy sidecars, owned by a 2-3 person platform group that nobody else on the engineering org wants to think about.

In regulated industries (banks, healthcare, government), it shows up as the answer to a compliance checklist: SPIFFE identities mapped to audit logs, mTLS everywhere, AuthorizationPolicies gating which namespace can talk to which. In GPU/AI shops, it shows up — increasingly in ambient mode — wrapping fleets of model-serving pods that can't afford 150 MB of sidecar RAM per replica.

You will recognize it in incidents as the thing that suddenly becomes the prime suspect: "did istiod push bad config?", "are the sidecars OOMing?", "is mTLS broken between namespaces again?"

## Best practices

### 1. Roll out the mesh namespace by namespace, never cluster-wide
**Do:** Label one low-stakes namespace `istio-injection=enabled` (or `linkerd.io/inject=enabled`), let it bake for a week, then expand. Keep a `meshed` and `unmeshed` SLO dashboard side by side.
**Why:** A cluster-wide injection flip means every pod restart at once, sidecar OOMs you didn't predict, and zero baseline to compare against. Real rollouts that skipped this step have rolled back within hours.
**Avoid:** `kubectl label namespace --all istio-injection=enabled` on a Friday.

### 2. Roll mTLS to STRICT in three phases, not one
**Do:** Start with `PeerAuthentication: PERMISSIVE` mesh-wide (accepts both plaintext and mTLS). Watch `istio_requests_total{connection_security_policy="mutual_tls"}` climb to ~100% per namespace. Only then flip that namespace to `STRICT`.
**Why:** A direct flip to STRICT silently breaks any workload that isn't meshed yet — including CronJobs, init containers, and that one legacy VM nobody told you about. PERMISSIVE gives you a metric to prove readiness.
**Avoid:** Setting `mtls.mode: STRICT` at the mesh root and finding out at 2 a.m. which workload was unmeshed.

### 3. Scope every sidecar with a `Sidecar` resource (Istio) or equivalent
**Do:** Define a `Sidecar` CRD per namespace listing only the upstream services that namespace actually calls. In Linkerd, use `Server` and `ServerAuthorization`.
**Why:** Default Istio gives every sidecar the full mesh's CDS/EDS — in a 5k-pod cluster that's hundreds of MB of config per proxy and istiod push storms on every Service churn. Scoped sidecars cut memory 5-10x and make istiod CPU survivable.
**Avoid:** Treating the default "every proxy knows everything" as fine because it works at 50 pods.

### 4. Manage all mesh config in GitOps with a separate repo lifecycle
**Do:** Put `VirtualService`, `DestinationRule`, `AuthorizationPolicy`, `PeerAuthentication` in a `platform-mesh` repo synced by ArgoCD/Flux. Require PR review from the platform team. Tag releases the same way you tag the control plane version.
**Why:** Mesh CRDs are blast-radius config — a bad `VirtualService` host match can blackhole an entire service. Git history plus required review is how you avoid the 3 a.m. "who changed the routing?" question.
**Avoid:** App teams pushing `VirtualService` into their own Helm charts with no central review.

### 5. Choose your mesh by team size and feature surface, not hype
**Do:** Pick **Linkerd** if you have <5 platform engineers and need mTLS + observability + simple traffic split. Pick **Istio (ambient mode)** if you need WASM filters, ext_authz, multi-cluster, or you're already on Envoy elsewhere. Pick **Cilium Service Mesh** if you already run Cilium CNI and want eBPF-level integration.
**Why:** Linkerd's Rust micro-proxy is ~0.8 ms p99 overhead and has a config surface a single engineer can hold in their head. Istio's surface area requires a dedicated team but unlocks every L7 use case.
**Avoid:** Picking Istio because "everyone uses it" when your actual need is mTLS + a Grafana dashboard.

### 6. Budget per-hop latency and retries explicitly
**Do:** Set a per-hop p99 budget (e.g., 2 ms for the proxy itself). For retries, use `retryBudget` (Linkerd) or constrain `retries.attempts` to ≤2 and never enable retries on non-idempotent calls. Document the call-chain depth.
**Why:** Three retries at every hop in a 4-deep chain = 81 attempts during a brownout. This is a classic retry-storm amplification and has caused real outages.
**Avoid:** Copy-pasting `attempts: 3, retryOn: 5xx` into every VirtualService.

### 7. Right-size sidecar resources from real data, not defaults
**Do:** Default Istio sidecar requests 100m CPU / 128 Mi memory. Measure actual usage at p95 over a week (`container_memory_working_set_bytes{container="istio-proxy"}`) and set requests at p95, limits at 2x. Linkerd's micro-proxy is smaller — start at 50m / 64 Mi.
**Why:** Istio 1.24 measures ~0.20 vCPU and 60 MB at 1000 RPS per sidecar; at 200 pods that's 20 vCPU and 25 GB just for proxies. Wrong defaults waste cluster capacity or OOM-kill under load.
**Avoid:** Shipping defaults to a 1000-pod cluster.

### 8. Exempt initContainers, Jobs, and short-lived workloads from injection
**Do:** Use `sidecar.istio.io/inject: "false"` annotations on Jobs/CronJobs that need network egress before the sidecar is ready, or use Istio 1.7+'s `holdApplicationUntilProxyStarts`. For Jobs, also set `proxy.istio.io/config: '{"terminationDrainDuration": "0s"}'` so the sidecar exits cleanly.
**Why:** Without this, Jobs hang forever because the sidecar never sees a SIGTERM, and init containers fail because iptables redirects exist before the proxy is ready.
**Avoid:** Letting a CronJob silently leak pods every hour for a month.

### 9. Pin and patch the control plane on a known cadence
**Do:** Track the istiod / linkerd-control-plane version like you track Kubernetes itself. Subscribe to the security mailing list. Patch within 2 weeks of a CVE.
**Why:** Istiod has had remote-code-execution and DoS CVEs (e.g., CVE-2024-3727 chain via Envoy). An unpatched control plane is a privileged blast radius across every workload.
**Avoid:** "We installed Istio 1.18 in 2023 and it's been fine" two years later.

### 10. Use the mesh's identity for app calls, not hardcoded hostnames or IPs
**Do:** Call `http://payments.payments-ns.svc.cluster.local` (or the short `payments` form) and let the mesh resolve. Reference workloads in `AuthorizationPolicy` by SPIFFE ID / service account.
**Why:** Hardcoding pod IPs or external LB addresses bypasses the mesh entirely — no mTLS, no retries, no metrics. The most common cause of "why isn't this call meshed?" is a config string nobody noticed.
**Avoid:** Hardcoding upstream IPs "because DNS was flaky once."

## Anti-patterns to recognize

- **Meshing everything on day one**: Inject the sidecar into 100% of namespaces in week one to "get it over with." It fails because you have no baseline to A/B against and no isolated blast radius when something breaks; instead, mesh one namespace, measure, expand.
- **Using the mesh as a circuit breaker library replacement**: Treating outlier detection as identical to Hystrix-style circuit breaking. It fails because mesh circuit breakers are connection-level, not semantic — they don't know that "card declined" is a business response, not a failure; keep app-level resilience for app-level concerns.
- **Ignoring per-hop latency**: Adding a sidecar to a 50-microservice call chain because the per-hop overhead "is only 1 ms." It fails because 50 × 1 ms = 50 ms of pure proxy tax on top of work; budget latency end-to-end and consider ambient mode or trimming the call graph.
- **Mesh-as-API-gateway**: Using `VirtualService` for north-south ingress routing and skipping a proper gateway. It fails because mesh CRDs are tuned for east-west semantics and the team ends up reinventing rate limiting, WAF, and auth at L7; use Envoy Gateway or a real gateway in front.
- **Multi-cluster before single-cluster is solid**: Linking three clusters with east-west gateways before you have stable single-cluster SLOs. It fails because multi-cluster doubles the failure modes (cross-cluster cert trust, gateway availability, DNS); get one cluster fully meshed and observable first.
- **Letting istiod run unbounded**: No `Sidecar` resources, no `discoverySelectors`, no proxy scoping. It fails when the cluster grows past ~2k services and istiod CPU spikes during deploy windows, with xDS push latency climbing to 30+ seconds; scope aggressively and shard control plane if needed.
- **Ambient mode on day-one production**: Adopting Istio ambient because "no sidecars" sounds appealing, without testing ztunnel under load. It fails because ztunnel is per-node — one noisy pod can starve everything else on that node, and the cert-rotation bugs are real; pilot on staging clusters for 2-3 months first.

## Real-world usage patterns

**1. The compliance-driven rollout (financial services).** A regional bank, ~80 microservices on 3 EKS clusters, needs PCI-DSS "encrypted in transit between every component." They picked Linkerd for the small ops surface, enabled mTLS in PERMISSIVE for 6 weeks while remediating un-meshed CronJobs, then flipped STRICT one namespace per week. **Non-obvious lesson:** the longest tail of work wasn't the mesh — it was finding every batch job and legacy ETL pod that lived outside Kubernetes.

**2. The progressive-delivery platform (consumer SaaS).** A B2C product team uses Istio + Flagger for automated canary releases gated on `istio_request_duration_milliseconds`. Each PR to `main` triggers a 5%/25%/50%/100% traffic shift over 30 minutes, auto-rolled back if p99 regresses. **Non-obvious lesson:** the canary metric must be the *user-facing* p99, not the mesh's per-hop p99 — internal hops can look fine while user latency degrades from a downstream change.

**3. The multi-cluster failover (global e-commerce).** Three Istio clusters (us-east, eu-west, ap-south) joined as a single mesh via east-west gateways. Traffic is locality-routed; when the eu-west cluster drains, requests fail over to us-east transparently. **Non-obvious lesson:** the failover only works if every service has replicas in 2+ clusters *and* the `DestinationRule` `localityLbSetting` is configured — the default is "stay local," which means an empty cluster blackholes traffic.

**4. The AI inference fleet (ML platform).** A model-serving platform runs 4000+ GPU pods, each pulling 80 GB of GPU memory. They moved from sidecar Istio to ambient mode specifically to claw back ~150 MB × 4000 pods of RAM. **Non-obvious lesson:** the savings was real but the migration window was long — they ran sidecar and ambient side-by-side for 4 months to validate ztunnel under bursty inference traffic.

## Operational checklist

- **Monitoring**: Is `istio_requests_total` broken down by `response_code`, `response_flags`, `source_workload`, `destination_workload`? Is istiod's `pilot_xds_push_time` and `pilot_proxy_convergence_time` on a dashboard?
- **Failure handling**: What happens when istiod is down for 10 minutes? (Answer: existing proxies keep serving with last config; new pods fail. Have you tested this?)
- **Cert rotation**: Do you have an alert for `citadel_server_csr_count` flatlining? A stalled CA is a 24-hour-fuse outage.
- **503 triage runbook**: Can the on-call engineer read an Envoy access log and tell UH (no healthy upstream → check endpoints) from UF (connection refused → check destination port/TLS) from NR (no route → check VirtualService) in under 2 minutes?
- **Security**: Is `AuthorizationPolicy` default-deny in production namespaces, or default-allow? Has anyone audited the `AuthorizationPolicy` resources for `from.source.notNamespaces` typos?
- **Cost**: What % of cluster CPU and memory is "mesh tax" (sidecars + istiod)? Is that budgeted, or a surprise on the cloud bill?
- **Onboarding**: Can a new app team add their service to the mesh with a single label, or do they need a platform engineer in a meeting?
- **Upgrades**: Is there a documented control-plane upgrade procedure with rollback? Has it been rehearsed in staging in the last quarter?
- **Exemptions**: Is there a clear list of workloads that must NOT be injected (CronJobs that exec'd, legacy network-policy edge cases), and is it in code?

## How this topic typically evolves in a codebase

Teams usually start by meshing one "interesting" service to demo retries or get a Grafana service map. The mesh sits in a shared `istio-system` namespace, configured by hand. Within 6-12 months it spreads to most production namespaces, mTLS is in PERMISSIVE, and someone has built a small internal abstraction over `VirtualService` because the raw CRDs are too verbose for app teams.

The painful migration point arrives at ~150-300 services or ~2k pods: istiod starts thrashing, sidecar memory adds up to a measurable chunk of cluster spend, and the platform team rediscovers `Sidecar` scoping, `discoverySelectors`, and per-namespace control-plane sharding. Around the same time, ambient mode becomes tempting — but migrating is its own multi-month project.

Mature deployments end up looking similar regardless of vendor: GitOps-managed config, narrow per-namespace scopes, default-deny authz, golden-path Helm charts for app teams, and a small platform group that knows xDS by heart. The teams that walk away from a mesh are usually those who adopted it before they needed it — 10-service shops who replace it with a managed gateway plus per-language mTLS libraries and reclaim 20% of cluster capacity.

## Further reading

- [Istio Performance and Scalability docs](https://istio.io/latest/docs/ops/deployment/performance-and-scalability/) — official baseline for sidecar CPU/memory per RPS; the only number you should quote without a footnote.
- [Linkerd vs Ambient Mesh 2025 Benchmarks](https://linkerd.io/2025/04/24/linkerd-vs-ambient-mesh-2025-benchmarks/) — Buoyant's own numbers, biased but methodologically clear; pair with your own benchmark on your workload.
- [Envoy response flags reference](https://www.envoyproxy.io/docs/envoy/latest/configuration/observability/access_log/usage) — bookmark for on-call; UH/UF/NR/UO/UC/DC are the alphabet of mesh debugging.
- [Tetrate's "Choosing the Right Istio Architecture"](https://tetrate.io/blog/choosing-the-right-istio-architecture-a-data-driven-guide-to-ambient-sidecar-and-hybrid-deployment-models) — practical sidecar vs ambient vs hybrid decision framework with real numbers.
- [Kubernetes Gateway API (GAMMA)](https://gateway-api.sigs.k8s.io/mesh/) — the portable successor to vendor mesh CRDs; understand it before committing to vendor lock-in.
- [Debugging 503 UF in Envoy with Istio and ALB Idle Timeouts](https://medium.com/@gopiaws98/debugging-503-uf-in-envoy-with-istio-and-alb-idle-timeouts-d284ca2cd647) — a representative real-world incident write-up that teaches the timeout-mismatch class of bugs.
