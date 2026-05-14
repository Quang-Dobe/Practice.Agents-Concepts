# Blue-Green Deployment

> A release strategy where you keep two identical production environments side by side, deploy the new version to the idle one, and flip a single switch to send users there — with the old one still warm for instant rollback.

## The 30-second version
Blue-green deployment is the answer to "how do I ship a new version without users seeing downtime, and how do I undo it in seconds if it breaks?" You run two full copies of your app — call them **blue** (the live one) and **green** (the standby). You deploy the new build to green, smoke-test it while no real users touch it, then point your load balancer or DNS at green. If green misbehaves, you flip the switch back to blue. The deploy is just the switch — the risky part is already done.

## The mental model
Picture a stage with two identical sets behind a curtain. The audience (your users) is watching set A — that is blue. Backstage, the crew is dressing set B for the next act — that is green. When set B is ready, the curtain shifts and the audience is now looking at B. Set A is still standing, fully dressed, lights on. If a prop falls on B mid-scene, you just shift the curtain back. No one rebuilds a set in front of a live audience.

In cloud terms, the "curtain" is usually a load balancer with two target groups, a DNS record, or a weighted route. The "sets" are two parallel fleets (VMs, containers, Lambda aliases) running the same plumbing on the same data, just different application versions.

```
        ┌───────────┐
users ──►  Router   │── 100% ──►  BLUE  (v1.4, live)
        │ (ALB/DNS) │── 0%   ──►  GREEN (v1.5, staged)
        └───────────┘
                          flip ▲
                          ─────┘
        ┌───────────┐
users ──►  Router   │── 0%   ──►  BLUE  (v1.4, standby)
        │ (ALB/DNS) │── 100% ──►  GREEN (v1.5, live)
        └───────────┘
```

## What it is NOT
- **Not a rolling update.** Rolling replaces instances in place, one batch at a time — there is no second full environment and no instant rollback target.
- **Not a canary release.** Canary sends a small percentage of real users to the new version to measure it; blue-green is all-or-nothing on the cutover.
- **Not feature flags.** Flags toggle behavior inside one running build; blue-green swaps the build itself.
- **Not a staging environment.** Staging is a permanently separate, lower-fidelity world. In blue-green both environments are production.

## When you would reach for it
- You need near-zero downtime and your users notice every hiccup (payments, checkout, public APIs).
- You want a rollback measured in seconds, not in "rebuild and redeploy."
- Your release contains a risky binary change you cannot easily reverse with a flag.
- Your platform makes a second environment cheap and short-lived — ECS, Kubernetes, Lambda aliases, an ALB with weighted target groups.

## When you would NOT reach for it
- The app owns stateful, schema-breaking changes to a shared database — both colors talk to the same data, so a non-backward-compatible migration will break the standby.
- Doubling capacity is genuinely too expensive (large stateful clusters, GPU fleets).
- You need to validate the change against a slice of real users before full rollout — that is canary's job.
- Long-lived sticky sessions, websockets, or in-memory state make the cutover messy without extra connection draining.

## Key vocabulary (just enough to keep reading)
- **Blue / Green** — the two environments. Names are arbitrary; some teams use A/B or current/next.
- **Cutover** — the moment traffic shifts from one color to the other.
- **Target group** — in AWS ALB terms, the pool of instances behind a listener. Blue and green are two target groups.
- **Weighted routing** — splitting traffic by percentage across target groups; blue-green typically uses 100/0 then 0/100.
- **Drain / connection draining** — letting in-flight requests on the old color finish before shutting it down.
- **Rollback** — flipping the switch back to the previous color when something goes wrong.
- **Cutover window** — the short period both environments serve real traffic during the flip.
- **Backward-compatible migration** — a schema or contract change that lets old and new code coexist; the unspoken prerequisite for clean blue-green on a shared database.

## What's next
`02-deep-dive.md` answers What / Where / When / How / Why in detail — the cutover mechanics, the database-migration problem, how AWS CodeDeploy and Kubernetes implement this, and where blue-green sits on the spectrum with rolling and canary.
