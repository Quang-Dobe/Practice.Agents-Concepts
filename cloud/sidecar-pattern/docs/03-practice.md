# Sidecar Pattern — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In any Kubernetes-based platform team with more than a handful of services, the sidecar is the workhorse you trip over weekly. It is the Envoy that fronts every Pod in an Istio mesh, the Vault Agent that materializes database credentials onto an `emptyDir`, the Cloud SQL Auth Proxy that lets your app think Postgres is on localhost, and the Fluent Bit that ships logs because somebody added a sidecar before checking whether a DaemonSet would do.

In a typical B2B SaaS backend, sidecars sit between the application and three external worlds: identity (mTLS, SPIFFE, workload identity), data (managed DB IAM auth, secrets), and observability (logs, traces, metrics). On-call pages for "service is up but 502" are very often a sidecar problem, not an app problem.

In a regulated environment (fintech, healthcare), sidecars are how you get compliance features — encryption in transit, audit logs, secret rotation — onto legacy services nobody is allowed to rewrite. The sidecar becomes the compliance boundary.

In a Kubernetes Job or CronJob fleet, sidecars are the thing that historically broke everything, because the Job never finished while Envoy kept running. This is the textbook example of why native sidecars (`restartPolicy: Always` on init containers) matter.

## Best practices

### 1. Set memory limits, leave CPU limits off (or generous) for proxy sidecars
**Do:** Set `resources.requests` and `resources.limits.memory` on every sidecar. For an Istio Envoy at moderate traffic, request 100m CPU / 128Mi memory and limit memory at 512Mi–1Gi based on observed RSS. Leave CPU `limits` unset, or at least well above request.
**Why:** CPU throttling on the proxy translates directly into application tail latency — the request can't leave the Pod until Envoy moves it. Throttled sidecars look like "the app is slow" in dashboards. Unbounded memory, however, causes OOMKills that cascade across the namespace.
**Avoid:** Copy-pasting `cpu: 100m` as a hard limit onto every sidecar. That's the single most common cause of mysterious p99 spikes in Istio meshes.

### 2. Use native sidecars (`initContainers` with `restartPolicy: Always`) on K8s 1.29+
**Do:** Run Kubernetes 1.29 or later (GA in 1.33), and configure your mesh / agent to emit native sidecars. For Istio: `values.pilot.env.ENABLE_NATIVE_SIDECARS=true`. For Vault Agent Injector: set `vault.hashicorp.com/agent-init-first` appropriately and prefer the native sidecar mode in recent chart versions.
**Why:** Solves the two classic foot-guns in one stroke — startup race (app calls out before proxy is ready) and shutdown drop (proxy dies while app is still draining). Also fixes Jobs that hang forever because the sidecar refuses to exit.
**Avoid:** The `preStop: sleep 30` and `holdApplicationUntilProxyStarts` workarounds on new clusters. They were the right answer in 2022; in 2026 they are tech debt.

### 3. Scope sidecar config to what each workload actually needs
**Do:** Use Istio `Sidecar` resources and `exportTo` to restrict each Envoy's view of the mesh to the services it actually talks to. In a 2000-endpoint cluster, scoping a service that only talks to 20 endpoints can drop its sidecar memory from ~60 MB to ~45 MB.
**Why:** Default Envoy gets the entire mesh's xDS config pushed to it. Multiply 60 MB of config × 500 Pods and you have 30 GB of cluster RAM dedicated to telling each Pod about services it will never call.
**Avoid:** Treating "the mesh is too expensive" as an inherent property of sidecars. Often the answer is two YAML files, not migrating to ambient.

### 4. Talk over `localhost` or a Unix domain socket, not over a Service
**Do:** App container connects to `127.0.0.1:<port>` or `unix:///var/run/<x>.sock` exposed by the sidecar. Use a shared `emptyDir` for socket files.
**Why:** A Service round-trip leaves the node, hits kube-proxy/iptables, and breaks the per-Pod identity guarantee — defeating the entire reason for a sidecar. Localhost is loopback-fast and shares the Pod's network namespace.
**Avoid:** "We exposed the sidecar as a ClusterIP because it was easier to test." That's now a fleet-wide proxy, not a sidecar.

### 5. Prefer DaemonSets for stdout/stderr log shipping
**Do:** Have the app log to stdout/stderr; let the container runtime write to `/var/log/containers/*.log` on the node; run one Fluent Bit per node as a DaemonSet to ship.
**Why:** A sidecar log shipper per Pod multiplies a 40-50 MB process by the Pod count on the node. A 60-Pod node pays ~2.5 GB for sidecar Fluent Bits where one DaemonSet costs ~50 MB.
**Avoid:** Reaching for a Fluent Bit sidecar by default. Use it only when you need per-Pod identity in the shipping path, or when the app writes to a file inside the container instead of stdout.

### 6. Define ordering and readiness explicitly
**Do:** Give the sidecar a `startupProbe` and `readinessProbe`. With native sidecars, the kubelet won't start the main container until the sidecar's startup probe passes, and the Pod won't go `Ready` until the sidecar's readiness probe passes.
**Why:** Without probes, "started" means "the process is running" — which for Envoy means iptables redirects traffic into a proxy that hasn't loaded its config yet. First requests fail with `connection refused` or `503 NR`.
**Avoid:** Relying on Pod-level readiness alone. The Pod can be Ready while Envoy is still receiving its initial xDS push.

### 7. Pin the sidecar image and treat upgrades as a deployment
**Do:** Pin the proxy/agent image by digest in the injection template. Roll mesh upgrades via Istio revision tags (`istio.io/rev`) so you can canary new sidecar versions to one namespace at a time.
**Why:** A bad sidecar image gets injected into every new Pod across the cluster the moment the webhook updates. Without revisions, "rollback the mesh" means deleting every Pod.
**Avoid:** Running the mesh control plane on `latest` or auto-updating the webhook config. The blast radius is the entire cluster.

### 8. Plan for the admission webhook being down
**Do:** Set the injector webhook's `failurePolicy` deliberately. `Fail` blocks new Pods if `istiod` is down (safer for security); `Ignore` admits unsidecared Pods (safer for availability). Document the choice. Run istiod with PDBs and at least 3 replicas across zones.
**Why:** A single-replica `istiod` in CrashLoop on a Friday evening is the canonical sidecar outage. With `Fail`, no new Pods start anywhere. With `Ignore`, Pods come up without mTLS, silently breaking compliance.
**Avoid:** Leaving `failurePolicy` at chart defaults without knowing what they are.

### 9. For Vault Agent / secret sidecars, write to `tmpfs` and signal the app
**Do:** Mount the shared `emptyDir` with `medium: Memory` (tmpfs) so rotated secrets never touch disk. Use `vault.hashicorp.com/agent-inject-command` to send the app SIGHUP, or have the app inotify-watch the secret file.
**Why:** Secrets on a disk-backed `emptyDir` survive container restarts in node memory and can be read by anyone with debug access to the node. Apps that read the secret once at startup never see rotations and break two hours later when the lease expires.
**Avoid:** Reading the secret file at startup and caching it forever. The whole point of the sidecar was rotation.

### 10. Treat the sidecar itself as a first-class observable
**Do:** Scrape the sidecar's own metrics endpoint (Envoy: `:15090/stats/prometheus`, Vault Agent: `:8200/v1/sys/metrics`). Alert on sidecar CPU throttling, sidecar memory near limit, xDS push errors, and certificate rotation failures.
**Why:** When the sidecar is unhealthy, the app *looks* unhealthy. Engineers spend hours debugging the app before someone thinks to check `istio-proxy`. Dashboards that surface sidecar health by default cut MTTR in half.
**Avoid:** "The sidecar is the platform team's problem." It's in your Pod; it's in your blast radius.

## Anti-patterns to recognize

- **The Swiss-Army-Pod**: A Pod with five sidecars — Envoy, Vault Agent, Fluent Bit, OTel Collector, Cloud SQL Proxy. Each made sense in isolation; together they consume more RAM than the app and quadruple the startup time. Better: consolidate via the OTel Collector (logs + metrics + traces), and move log shipping to a node DaemonSet.
- **Business logic in the sidecar**: Smuggling request validation, feature flags, or authorization rules into Envoy via Lua/WASM filters because "it's faster than a deploy." Six months later nobody knows where the rule lives, and the platform team owns app behavior. Better: keep cross-cutting concerns in the sidecar; keep domain decisions in the app or a dedicated policy service (OPA, Cedar).
- **Sidecar on `hostNetwork: true` Pods**: iptables redirect rules are written into the Pod's netns; `hostNetwork` Pods bypass them and the sidecar sees nothing. Traffic flows unencrypted while dashboards report "mesh covered." Better: exclude host-network workloads from injection explicitly, and document it.
- **Default resources on every sidecar**: Copying the same `100m / 128Mi` request onto every Pod regardless of traffic. Tiny services waste capacity; high-RPS services get OOMKilled. Better: tier sidecar resource profiles (low/med/high traffic) and assign per workload.
- **Sidecar as a substitute for an SDK**: Putting a 200 MB language-runtime sidecar next to every microservice so they can call a "client library" over localhost. The sidecar is now bigger than the app. Better: use a real SDK, or move to Dapr only when the polyglot tax is real.
- **Ignoring Job/CronJob semantics**: Running mesh-injected Jobs on a pre-1.29 cluster. The Job's main container exits, the sidecar keeps running, the Job sits in `Active` forever, the next scheduled run piles on, and eventually the namespace runs out of Pods. Better: native sidecars, or annotate Jobs to skip injection.
- **Webhook with single-replica istiod**: One control-plane Pod, no PDB. A node drain takes down injection; new Pods either fail admission or come up bare. Better: 3-replica istiod with topology spread and a PDB, monitored as a Tier-0 service.

## Real-world usage patterns

- **Polyglot fintech platform (~300 services, 6 languages).** Istio sidecars give every service mTLS with SPIFFE identities and uniform retry/circuit-breaker behavior. App teams ship in Go, Java, Python, Rust without writing a single line of TLS code. Non-obvious lesson: the win wasn't the mesh features, it was that the security team stopped reviewing TLS code in every PR — the sidecar became the compliance contract.

- **E-commerce checkout on GKE (~2000 RPS sustained).** Cloud SQL Auth Proxy as a sidecar on every checkout Pod. App connects to `localhost:5432` as if Postgres were local; the proxy handles IAM auth and connection encryption. Non-obvious lesson: the sidecar's connection pool sits in front of the app's connection pool — you have two pools to size, and getting either wrong burns connections on the DB side.

- **Healthcare data platform with frequent secret rotation.** Vault Agent sidecar pulls database credentials with a 1-hour TTL, renders them to a tmpfs file, and sends SIGHUP to the app on rotation. Non-obvious lesson: the app must reload its connection pool on SIGHUP — many ORMs cache the connection string at startup and never re-read it, which makes the whole rotation pipeline silently broken until the next deploy.

- **Migration from sidecar to ambient mesh (mid-size platform, ~500 Pods).** Team measured 12% of cluster memory going to Envoy sidecars. Moved L4 traffic to Istio ambient (ztunnel as a DaemonSet); kept sidecars only on the ~20 services needing L7 features via waypoints. Non-obvious lesson: the migration is per-namespace and reversible, but Telemetry shape changes — dashboards that aggregated by `source_workload` had to be rewritten because ztunnel reports differently than Envoy.

## Operational checklist

- Are sidecar CPU requests sized from real usage and CPU limits either unset or generous?
- Are memory limits set on every sidecar, with alerts at 80% of limit?
- Is the cluster on Kubernetes ≥ 1.29 and emitting native sidecars (verify with `kubectl get pod -o yaml | grep -A2 initContainers`)?
- Is there a synthetic test that deletes a Pod under load and counts 5xxs to prove shutdown ordering works?
- Do Jobs/CronJobs actually reach `Completed`, or are they hanging on sidecars?
- Is the injection webhook's `failurePolicy` an intentional choice, documented in the runbook?
- Is `istiod` / the injector control plane running ≥ 3 replicas with a PDB?
- Are sidecar-specific metrics (xDS push errors, certificate expiry, Envoy CPU throttling) on the on-call dashboard?
- For Vault sidecars: is the shared volume tmpfs, and does the app actually reload on rotation?
- Does the team have a documented threshold — e.g. "if sidecar memory exceeds 10% of cluster RAM, evaluate ambient"?

## How this topic typically evolves in a codebase

Teams start by adopting one sidecar — usually a service mesh or a Cloud SQL Proxy — for one obvious win (mTLS, IAM auth). For the first year it feels free: app teams don't know the sidecar exists, the platform team owns it, everyone is happy.

Around year two the sprawl appears. A logging sidecar lands because someone wanted custom parsing. A Vault Agent gets added for secrets. An OTel Collector for traces. Each addition is locally rational; collectively the Pod's startup time has doubled and 30% of the cluster's memory is sidecars. This is when teams either consolidate aggressively (one OTel Collector replacing three shippers, log shipping moved to a DaemonSet) or migrate to a sidecarless data plane for the bulk of L4 traffic.

The painful migration point is *not* the technical move to ambient or eBPF — it's untangling the implicit dependencies. Apps assume Envoy is rewriting their headers, dashboards assume Envoy metric names, security policies assume per-Pod proxy identity. The sidecar was invisible while it worked; making it visible enough to remove takes a quarter of platform work. Plan for that cost early.

## Further reading

- [Kubernetes — Sidecar Containers (official docs)](https://kubernetes.io/docs/concepts/workloads/pods/sidecar-containers/) — the canonical reference for native sidecars and lifecycle semantics.
- [KEP-753 Sidecar Containers](https://github.com/kubernetes/enhancements/blob/master/keps/sig-node/753-sidecar-containers/README.md) — the design doc; explains *why* `initContainers + restartPolicy: Always` and not a new field.
- [Istio — Sidecar or ambient?](https://istio.io/latest/docs/overview/dataplane-modes/) — Istio's own framing of the tradeoff, with current performance numbers.
- [Burns & Oppenheimer — Design patterns for container-based distributed systems (2016)](https://www.usenix.org/system/files/conference/hotcloud16/hotcloud16_burns.pdf) — the paper that named sidecar/ambassador/adapter; still the clearest taxonomy.
- [iximiuz — Making sense of native sidecar containers in Kubernetes](https://newsletter.iximiuz.com/posts/making-sense-out-of-native-sidecar-containers-in-kubernetes) — best practical walkthrough of the new sidecar model with shutdown ordering diagrams.
- [HashiCorp — Vault Agent Injector annotations](https://developer.hashicorp.com/vault/docs/deploy/kubernetes/injector/annotations) — exhaustive reference for the injection annotations you will inevitably need to look up.
