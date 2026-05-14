# Blue-Green Deployment — Practice

> Builds on `01-overview.md` and `02-deep-dive.md § How`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, blue-green is the strategy your platform team picks for the customer-facing API tier — the one whose error budget is most visible. The user-data store stays single-tenant and shared; only the stateless compute fleet doubles up during a release. It is wired into CI/CD as a two-step pipeline: "deploy to idle color" runs nightly, "cut over" is a gated button a release manager clicks.

In an e-commerce setup it tends to live on the checkout path specifically, where a 30-second outage is a measurable revenue dent. Other services (catalog, search, recommendations) often use rolling or canary; checkout uses blue-green because the rollback button matters more than the bake time.

You also see it as a *default* in opinionated platforms: AWS Lambda aliases with `RoutingConfig`, ECS native blue/green (GA since July 2025), Cloud Run revisions, Azure App Service deployment slots, Heroku `preboot`. In those shops nobody calls it "blue-green" — they call it "the deploy" and the pattern is invisible until something breaks.

## Best practices

### 1. Gate the cutover on real health, not on HTTP 200
**Do:** Make the readiness check exercise the DB pool, the cache, the message broker, and at least one downstream HTTP call. Add a synthetic transaction (login → fetch profile → write event) that runs against the idle color via a separate test listener before the switch.
**Why:** A `/healthz` that returns 200 unconditionally lies. The classic failure is green coming up with an empty DB connection pool or a stale secret; the cutover happens, the pool fills with dead connections, and the entire fleet 5xx's in unison.
**Avoid:** A health endpoint that only confirms the process is running.

### 2. Warm caches and JITs before flipping
**Do:** Replay a few minutes of mirrored traffic, hit the top-N hot endpoints, or run a startup probe that primes the in-process cache and triggers JIT compilation (.NET ReadyToRun, JVM tiered compilation). Only then mark the targets healthy.
**Why:** A cold green fleet hitting full prod traffic spikes p99, blows out downstream connection pools, and looks like a regression that "only happens after deploy." It is not the new code; it is the cold start.
**Avoid:** Treating "container started" as "ready to serve."

### 3. Size the drain window to your actual p99, not the default
**Do:** Measure the slowest legitimate request (file uploads, report exports, long polls). Set ALB `deregistration_delay.timeout_seconds`, Kubernetes `terminationGracePeriodSeconds`, and your app's SIGTERM handler all to the same value, and make sure it exceeds that p99 by ~20%.
**Why:** The 300-second AWS default is fine for typical APIs but disastrous for a 10-minute CSV export. Mismatched timeouts give you 502s at the edge while the user watches a progress bar.
**Avoid:** Leaving the default and hoping. Avoid even worse: setting `terminationGracePeriodSeconds` longer than the drain window — the pod dies before the LB stops sending to it.

### 4. Practice expand-contract for every schema change
**Do:** Split each schema migration into three deploys: **expand** (add new column/table, dual-write, both code versions tolerate it), **migrate** (backfill + switch reads), **contract** (drop the old column in a *later* release). Each deploy is independently blue-green-safe.
**Why:** Blue and green share one database. A `DROP COLUMN` or a `NOT NULL` rename run at cutover time will crash whichever color reads the old shape — usually blue, mid-drain, in front of real users.
**Avoid:** Bundling the migration into the same deploy as the code that needs it.

### 5. Treat feature flags as the *complement*, not the substitute
**Do:** Ship risky business logic dark behind a flag and use blue-green for the binary swap. Flip the flag separately, after the cutover settles.
**Why:** Blue-green gives you instant *infra* rollback; flags give you instant *behavior* rollback without redeploying. You want both: one rolls back the version, the other rolls back the feature.
**Avoid:** Using blue-green as the only rollback knob for behavior changes — you end up reverting an entire release to undo one bad copy change.

### 6. Push session and stateful work out of the app tier
**Do:** Sessions in Redis or a JWT, idempotency keys in DynamoDB or Postgres, WebSocket fan-out via a broker (Redis pub/sub, NATS, MQTT). Background workers should use leader election (Redis lock, Kubernetes `Lease`) so only one color runs the cron.
**Why:** Sticky-cookie sessions collapse the moment blue's tasks deregister; in-process WebSocket state evaporates; both colors will happily run the nightly billing job twice.
**Avoid:** Sticky sessions as the primary state mechanism. They are a useful fallback for routing affinity, not a session store.

### 7. Keep the old color warm only as long as rollback is cheap
**Do:** Default bake / termination window: 1 hour for high-traffic APIs, up to 24 hours for low-traffic critical paths. Automate teardown — don't leave it to a human ticket. ECS `terminationWaitTimeInMinutes` and Argo Rollouts `scaleDownDelaySeconds` both exist for this.
**Why:** After ~1 hour, "rollback to blue" stops being safe anyway — new writes have landed in the shared DB and old code may not understand them. You are paying double infra for an option you can't actually exercise.
**Avoid:** Leaving the idle color running indefinitely "just in case."

### 8. Build observability that survives the flip
**Do:** Tag every metric, log, and trace span with `deploy_color` (`blue` / `green`) and `version`. Build one dashboard with both colors side by side: request rate, 5xx rate, p50/p95/p99, downstream error rate. Alerts should compare *post-cutover green* to *pre-cutover blue baseline*, not to a fixed threshold.
**Why:** Without the split view, a regression on green looks like a global degradation and your on-call rolls back things they shouldn't. With it, "green's p99 is 2x blue's at the same RPS" is a one-glance diagnosis.
**Avoid:** Alerts that fire on every cutover because traffic momentarily routes to two backends — they get muted, and then real incidents get muted with them.

### 9. Automate the rollback button, then test it on a Wednesday
**Do:** A single command — `deploy rollback <service>` — that re-flips the listener and rewinds the bake timer. Run a game-day quarterly: deploy a known-broken build, verify the rollback completes in under 60 seconds.
**Why:** Untested rollback paths bit-rot. The IAM permission expires, the script references a renamed target group, the new engineer doesn't know the command. You find out during a real incident.
**Avoid:** "We can roll back if needed" as a verbal claim with no runbook.

### 10. Mind the singletons: crons, consumers, migrations
**Do:** Pin background workers to one color (label-selector or a `worker=true` task definition that only runs on the active fleet). For Kafka consumers, use one `group.id` and let the broker partition; do not run two groups in parallel.
**Why:** During the bake window both colors are alive. A naive setup double-processes events, double-sends emails, or races on a shared lock. The customer sees two charge confirmations.
**Avoid:** Assuming the load balancer is the only traffic source. Cron and message consumers don't go through the LB.

## Anti-patterns to recognize

- **Schema migration inside the cutover.** Running `flyway migrate` as a `BeforeAllowTraffic` hook. It breaks blue mid-drain because blue's binary doesn't know the new shape. Use expand-contract and run migrations *between* deploys, not during them.
- **"Deploy once and forget" blue-green.** The bake window runs forever because nobody wired up teardown. The bill doubles silently and the "idle" fleet drifts out of date (stale AMIs, expired certs) until rollback is no longer possible. Automate the scale-to-zero.
- **Blue-green on a stateful service.** Running it in front of a database, a stateful Kafka Streams app, or a per-node in-memory game server. The "second environment" cannot share state cheaply, so the cutover loses sessions or duplicates work. Use rolling with surge for stateful tiers.
- **Blue-green as a substitute for canary on a high-risk change.** Atomic 100% cutover means a bad release hits *every* user simultaneously. For ML model rollouts, pricing-engine changes, or auth-flow rewrites, you want a 1%/5%/25% canary first. Blue-green is for the *swap*, not for the *validation*.
- **DNS-based switch with a 300-second TTL.** Route 53 weighted records are fine for region failover, terrible for app deploys: client resolvers cache aggressively and the cutover smears over minutes. Use an L7 load balancer with a real atomic listener change.
- **Health check that calls itself.** `/healthz` returns 200 from a controller that does nothing. The deploy passes every gate and then explodes on the first real request. The probe must touch the dependencies the request path touches.
- **Sticky sessions doing the heavy lifting.** Affinity cookies are a routing optimization, not a state store. The instant blue's targets deregister, the affinity is meaningless and the session is gone.

## Real-world usage patterns

- **Payments API behind an ALB, ECS Fargate, expand-contract migrations.** A mid-size fintech runs the card-auth path as a 12-task ECS service, native ECS blue/green, 1-hour bake, automated rollback alarm on 5xx > 0.5% sustained 60s. Non-obvious lesson: their hardest bug wasn't the cutover, it was the *background* idempotency-key cleaner running on both colors during bake and deleting keys the other color was about to look up. Fix was a leader-election lock keyed on the active task set ARN.

- **B2B SaaS on Argo Rollouts with `previewService`.** A Kubernetes shop uses `blueGreen` strategy with a preview service exposed only to their CI synthetic-test suite. Cutover is gated on Datadog SLO metrics; rollback is automatic if SLO burn-rate alerts fire within the bake window. Non-obvious lesson: the synthetic tests had to authenticate as a real tenant, which meant the test tenant's data shape constrained what they could deploy — the suite became a load-bearing part of the contract.

- **Public consumer API on AWS Lambda with weighted aliases.** Mobile-app backend deploys via SAM, `RoutingConfig` shifts 10% → 50% → 100% over 10 minutes. Strictly speaking this is canary, but the team calls it blue-green because the previous version stays attached to the alias for instant rollback. Non-obvious lesson: cold-start latency on the new version spiked p99 even at 10% — fix was provisioned concurrency on the new version *before* shifting any traffic.

- **Internal admin tool on Azure App Service slots.** Slot swap is one click; rollback is the same click in reverse. Non-obvious lesson: app settings marked "deployment slot setting" stay with the slot during swap — the team learned this the hard way when a connection-string swap pointed production at the staging database for ~3 minutes.

## Operational checklist

- Is `deploy_color` and `version` on every metric, log, and trace? Can you draw a dashboard that overlays blue vs green for RPS, p99, and 5xx?
- Does your readiness probe touch the DB, cache, and at least one downstream — not just return 200?
- Is the drain timeout ≥ 1.2 × your measured p99? Is the app's SIGTERM handler aware of it?
- Is every schema change in the last 90 days expand-contract? Could the previous release run cleanly against today's schema?
- Is there a single command for rollback, owned by on-call, tested in the last quarter on a deliberately broken deploy?
- Do background workers, crons, and message consumers have a defined owner color during bake? Is the leader-election mechanism real?
- Is the idle color torn down automatically after the bake window, or does it linger? What does that linger cost per month?
- Does your alert config suppress flap during the cutover window without suppressing real regressions on green?

## How this topic typically evolves in a codebase

Teams usually start with rolling deploys on a single fleet, because that is the default in ECS, Kubernetes, and most PaaS. Blue-green arrives the first time a deploy causes an outage that a fast rollback would have prevented — typically a year or two in, after the system is load-bearing for revenue. The initial implementation is hand-rolled: a second target group, a script that flips the listener. It works.

The painful migration point comes when the database schema becomes too entangled for naive deploys. Teams discover they have been getting away with non-backward-compatible migrations only because rolling deploys were slow enough that the old code drained out before the new schema mattered. Blue-green exposes the assumption: blue and green coexist for the whole bake window, and any `ALTER` that breaks the old shape will page someone at 3 a.m. The fix is organizational, not technical — every PR with a migration now needs an expand-contract review, and that culture takes months to land.

Mature teams eventually layer canary on top of blue-green for the highest-risk changes (auth, billing, ML inference), keep blue-green for routine releases, and use rolling for stateless edge tiers where the cost saving matters. The strategy stops being "which deploy method do we use" and becomes "which method per service tier" — and the answer is in the service catalog, not in someone's head.

## Further reading

- [Martin Fowler — BlueGreenDeployment (2010)](https://martinfowler.com/bliki/BlueGreenDeployment.html) — the canonical short essay that named the pattern.
- Jez Humble & David Farley, *Continuous Delivery* (Addison-Wesley, 2010) — chapter 10 covers the original case for the pattern alongside expand-contract migrations.
- [AWS — Amazon ECS native blue/green deployments (GA July 2025)](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/deployment-type-bluegreen.html) — current AWS reference for the listener-flip mechanics and bake timer.
- [Argo Rollouts — Blue-Green Strategy](https://argo-rollouts.readthedocs.io/en/stable/features/bluegreen/) — the Kubernetes reference implementation, including `previewService` and analysis templates.
- [GitHub Engineering — Move fast and fix things (gh-ost)](https://github.blog/2016-08-01-gh-ost-github-s-online-schema-migration-tool-for-mysql/) — why online schema change is the non-obvious half of any blue-green strategy.
- [Charity Majors — Test in production](https://charity.wtf/2017/07/06/test-in-production-or-you-will/) — the case for observability-as-cutover-gate; reframes "is it healthy?" as "can you tell?"
