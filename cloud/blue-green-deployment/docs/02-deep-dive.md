# Blue-Green Deployment — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
Blue-green deployment is a release strategy in which two production-grade environments — conventionally labelled **blue** and **green** — exist in parallel, each running a single version of the application, and a routing primitive (load balancer target group, DNS record, Kubernetes `Service` selector, or service-mesh route) atomically shifts 100% of user traffic from one to the other. The deployment is decoupled from the cutover: code is installed and warmed on the idle pool first; the cutover is just a pointer flip. The previously-live environment is kept hot for a defined window so rollback is a second pointer flip rather than a redeploy.

It is the simplest member of the **all-or-nothing release** family (alongside *recreate*), as opposed to the *progressive delivery* family (canary, rolling, traffic-shaping).

### The core building blocks
- **Two homogeneous fleets** — same instance type, same network, same dependencies. They differ only in application version.
- **A traffic-switch primitive** — the atomic decision point. ALB listener rules pointing at one of two target groups; Route 53 weighted records; a Kubernetes `Service` whose `selector` label changes; an Envoy/Istio `VirtualService`; Cloud Run revision traffic tags.
- **A health gate** — readiness probes, smoke tests, or synthetic checks that must pass on the idle environment before the switch is allowed.
- **A drain mechanism** — connection draining / deregistration delay that lets in-flight requests on the old fleet finish after the switch.
- **A shared stateful tier** — usually one database, one cache, one message broker. Both colors read and write the same data, which is exactly what makes schema changes the hard part.

### How it relates to the broader landscape
Blue-green sits in the **deployment strategies** family next to *rolling*, *canary*, *recreate*, *A/B*, and *shadow*. Rolling replaces instances in-place batch by batch (no second environment, no instant rollback). Canary sends a percentage of real traffic to the new version to measure it (progressive, not atomic). Recreate stops blue then starts green (downtime, no parallel state). Blue-green is the strategy that optimises for **rollback speed** at the cost of **double infrastructure during the cutover window**.

## Where

### Where it lives in the stack
The mechanism lives at the **edge / L7 routing layer** of the application tier: ALB listener, ingress controller, service mesh, API gateway, or DNS. The two fleets sit one layer below, in the compute tier (EC2 ASG, ECS service, K8s `Deployment`, Lambda alias, container revision). The stateful tier (RDS, DynamoDB, Kafka, Redis) is almost always **shared** between the two colors — duplicating it is what turns a blue-green into a full disaster-recovery exercise.

### Where you typically encounter it
- **AWS ECS** — both the legacy CodeDeploy controller and the native ECS blue/green controller launched in July 2025 implement it as a first-class deployment type.
- **AWS Lambda** — `AWS::Lambda::Alias` with `RoutingConfig` shifts a fixed percentage between two versions; AWS SAM/CodeDeploy wraps this as a blue-green deploy.
- **Kubernetes via Argo Rollouts** — the `blueGreen` strategy with `activeService` and `previewService` fields.
- **Google Cloud Run** — every deploy creates an immutable revision; traffic tags route between them.
- **Heroku** — `preboot` is essentially a managed blue-green for dynos.
- **Azure App Service** — deployment slots with the "swap" operation are the same pattern.
- **On-prem IIS** — Application Request Routing (ARR) in front of two app pools.

### Ecosystem and tooling
- **For traffic switching** — AWS ALB / NLB, Route 53 weighted records, Envoy, Istio, NGINX ingress, HAProxy, Cloudflare load balancers.
- **For orchestration** — AWS CodeDeploy (`CodeDeployDefault.ECSAllAtOnce`, `LinearXPercentEveryYMinutes`, `Canary10Percent5Minutes`), Argo Rollouts, Flagger, Spinnaker, Harness, Octopus Deploy.
- **For schema migrations during cutover** — Liquibase, Flyway, Bytebase, Reshape (Postgres), gh-ost / pt-online-schema-change (MySQL).
- **For session/state handling** — Redis or DynamoDB as an out-of-process session store (so neither color owns the session); sticky cookies only as a fallback.

## When

### When the topic emerged and why
The term was popularised by Daniel Terhorst-North and later cemented in Jez Humble and David Farley's *Continuous Delivery* (2010). Before it, "deployment" meant Friday-night maintenance windows: stop the service, copy the new build, run migrations, pray. The shift to commodity virtualisation (EC2 in 2006, then containers) made spinning up a second identical environment for an hour genuinely cheap. Blue-green is the natural artifact of that economic shift: when capacity is rentable by the minute, "keep two of everything during the deploy" stops being absurd.

### When to use it in a project
Reach for it when:
- Your release contains a **single atomic binary or contract change** that is hard to feature-flag.
- Rollback time matters in **seconds, not minutes** (payments, checkout, public APIs, regulated systems).
- Your app tier is **stateless or near-stateless** — sessions live in Redis, not in memory.
- Your platform makes a second environment **cheap and short-lived** — ECS, Lambda, Cloud Run, Argo Rollouts.
- Your schema changes can be staged through **expand-contract** so both colors run safely against the same database.

### When NOT to use it
Avoid it when:
- The release ships a **non-backward-compatible schema migration**. A `DROP COLUMN` is fatal — blue will crash the moment green's migration runs.
- You need to **validate against real users before full rollout** — that is canary's job, not blue-green's.
- The system holds **large in-memory state per node** (long-lived websockets, in-process caches, game-server matches). The cutover window forces awkward drain logic.
- Doubling infrastructure is **prohibitive** — large stateful clusters, GPU fleets, on-prem capacity that cannot be conjured on demand.
- The team has **no automated smoke tests** for the idle environment. Without them, the green pool's "healthy" signal is a lie and the cutover just exposes broken code faster.

## How

### How it works under the hood
Walk the lifecycle for a typical ALB + ECS deployment:

1. **Steady state.** Listener rule forwards 100% of traffic to `tg-blue`. `tg-green` exists but has zero registered targets (or stale ones marked unhealthy).
2. **Provision green.** The deployment controller starts a new task set with the new task definition. Tasks register into `tg-green` and pass ALB health checks (typically 5 consecutive 200s on `/healthz` within `HealthCheckIntervalSeconds`).
3. **Test traffic (optional).** ECS native blue/green and CodeDeploy both support a *test listener* on a separate port, so the deployer or a CI job can hit green directly without touching production traffic. Argo Rollouts achieves this via `previewService`.
4. **Bake.** Both versions run; only blue serves users. CodeDeploy hooks (`BeforeAllowTraffic`) can run synthetic checks here.
5. **Cutover.** The controller calls `ModifyListener` (or rewrites the `Service` selector in K8s; or `gcloud run services update-traffic` in Cloud Run). From the next request onward, the production listener forwards to `tg-green`.
6. **Drain.** Old tasks enter `DRAINING`. The ALB stops sending new connections but waits `deregistration_delay.timeout_seconds` — default **300 seconds** on AWS ALB, configurable 0–3600 — for in-flight requests to finish.
7. **Keep blue warm.** ECS native blue/green has a `bake time` (default configurable; CodeDeploy `terminationWaitTimeInMinutes` was capped at 2880 / 48h). During this window, rollback is a single `ModifyListener` call back to `tg-blue`.
8. **Terminate or repurpose.** After bake time expires with no alarms, blue is scaled to zero or recycled as the next deploy's green.

```
   ┌──────────── ALB Listener :443 ────────────┐
   │                                           │
   │   forward → tg-blue   [100% prod]         │   step 1
   │   forward → tg-green  [test listener]     │   step 2–4
   │                                           │
   │   ─── ModifyListener (atomic) ───         │   step 5
   │                                           │
   │   forward → tg-green  [100% prod]         │   step 6–7
   │   tg-blue draining (5 min default)        │
   └───────────────────────────────────────────┘
```

In Argo Rollouts the same dance happens with two K8s `Service` objects: `activeService` and `previewService` both point at the v1 ReplicaSet at rest; on promotion, `activeService`'s selector is patched to the v2 hash and shortly after `previewService` is updated to match.

### Key trade-offs

| Design choice | What you gain | What you give up |
|---|---|---|
| Two full environments | Instant rollback; no in-place mutation | ~2x infra cost during the cutover window |
| Atomic 100% cutover | Simple mental model; no per-user variance | No early-warning sample; bad release hits all users at once |
| Shared database | One source of truth; no data drift | Every schema change must be backward compatible (expand-contract) |
| Single switch primitive | Easy to script and automate | Cutover correctness depends entirely on the router behaving atomically |
| Bake / termination window | Cheap rollback for a fixed period | Pays double infra for the bake duration |

### Common failure modes
- **Schema breakage on cutover.** Migration dropped a column blue still uses → blue 500s during drain.
- **In-flight long requests dropped.** Drain timeout shorter than the slowest request (file uploads, long-poll, streaming) → 502s at the edge.
- **Sticky session collapse.** ALB sticky cookies pinned users to blue tasks; after drain they get rebalanced and lose session unless session state lives in Redis/DynamoDB.
- **DNS-based switch with stale resolvers.** Route 53 weighted record cutover stretched over the full TTL; some clients keep hitting blue for minutes after teardown.
- **Connection pools cached on green.** Green came up before DB credentials rotated; pool full of dead connections; health checks pass on `/healthz` but real traffic 500s.
- **Asymmetric warm caches.** Blue's in-process caches were hot; green starts cold and the cutover spikes p99 latency and downstream load.
- **Singleton background jobs running on both colors.** A nightly cron, a Kafka consumer with a fixed `group.id` — both environments race or double-process. Needs an explicit leader-election or worker-pinning rule.

## Why

### Why it exists
It addresses two fundamental tensions: **deployment risk vs. release velocity** and **rollback cost vs. uptime**. Pre-cloud, those tensions resolved with maintenance windows. Once infra became elastic, the cheapest answer to "how do I make a release safely reversible in seconds?" turned out to be "have the previous version still running."

### Why it looks the way it does
The obvious alternative is **rolling deployment**: replace pods one at a time, no second environment. Rolling wins on cost — it reuses existing capacity. It loses on two axes: (1) the new and old versions coexist for the *entire* rollout duration, not just a drain window, so backward-compat is required for longer and more pairs of versions; (2) rollback is also a rolling process, so the recovery time grows with fleet size. Blue-green compresses the coexistence window to seconds-to-minutes and makes rollback O(1) — one API call — at the price of doubling infra for that window. For systems where a bad five minutes is more expensive than a few extra instance-hours, the trade is obviously correct.

The other obvious alternative is **canary**. Canary keeps cost lower than full blue-green and adds a real-traffic safety sample, but it requires per-request traffic-splitting infra, automated metric analysis, and the discipline to actually pause and bisect. Blue-green is the strategy you reach for when you want the rollback guarantee without paying the canary complexity tax.

### Why it matters now
As of 2026, the pattern is moving from "DIY with CodeDeploy" to **first-class platform primitive**. AWS shipped native ECS blue/green in July 2025, removing the CodeDeploy hop. Cloud Run revisions and Argo Rollouts have made it routine in serverless and Kubernetes respectively. The interesting frontier is no longer the cutover mechanic — that is solved — but the **database tier**: expand-contract migrations, online schema-change tools, and dual-write strategies are where most blue-green incidents now originate.

## Open questions / things to verify in practice
- What is the actual p99 request duration of my service? Does the drain timeout cover it, or do I need to raise `deregistration_delay.timeout_seconds`?
- Are all my schema migrations expand-contract? Can I prove the previous version's binary still runs cleanly against the post-migration schema?
- Where does session state live? If a user is in the middle of a multi-step flow when the cutover happens, do they notice?
- What is the cost of running blue alongside green for my chosen bake time? Is the rollback insurance worth that bill?
- Do my background workers, cron jobs, and event consumers behave correctly when both colors are running? Who holds the lease?
- Does my health check actually exercise the same code paths as production traffic, or just return 200 unconditionally?
