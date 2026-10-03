# Autoscaling — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
Autoscaling is a **closed-loop feedback controller** that adjusts the provisioned compute capacity of a workload, either the replica count (horizontal) or the per-replica resource allocation (vertical), so that an observed metric stays near a setpoint, within operator-defined bounds `[min, max]`. In control terms, the plant is your fleet, the sensor is a metrics pipeline, the actuator is a provisioning API (launch VM, create pod, patch resources), and the system has significant **dead time**: capacity requested now becomes useful capacity only after boot, image pull, and readiness. Scope matches the overview: cloud VM groups and Kubernetes workloads, not storage auto-growth.

### The core building blocks
- **Scaling target.** The thing being resized: an AWS Auto Scaling Group, Azure Virtual Machine Scale Set, GCP Managed Instance Group, or a Kubernetes `Deployment` / `StatefulSet` via its `/scale` subresource.
- **Metric source.** Where the signal comes from: CloudWatch, Azure Monitor, Cloud Monitoring, or the Kubernetes aggregated APIs `metrics.k8s.io` (usually metrics-server), `custom.metrics.k8s.io`, and `external.metrics.k8s.io`.
- **Policy.** The control law. Target tracking (hold metric at X), step/threshold rules (if metric > X add N), scheduled (cron-like), or predictive (forecast-driven).
- **Bounds.** `min`, `max`, and the current `desired` count. Bounds are the only hard guarantee you get. Everything else is best effort.
- **Damping.** Cooldowns, warmup periods, stabilization windows, and tolerance bands. These suppress oscillation in a system with lag.
- **Lifecycle hooks.** Launch/terminate hooks (ASG lifecycle hooks, Kubernetes `preStop`, `terminationGracePeriodSeconds`, PodDisruptionBudgets) that make adding and removing capacity safe.
- **Layered controllers (Kubernetes).** Pod-level (HPA, VPA, KEDA) and node-level (Cluster Autoscaler, Karpenter) controllers that cooperate only through the scheduler's "Pending pod" signal.

### How it relates to the broader landscape
Autoscaling belongs to the **elasticity / capacity management** family, next to capacity planning (offline, human-driven), load balancing (distributes load across existing capacity), and admission control / load shedding (rejects work when capacity is exhausted). Serverless platforms (AWS Lambda, Cloud Run, Azure Container Apps) are autoscaling with the knobs hidden: per-request or per-concurrency scaling, including to zero. The closest siblings are **rate limiting**, which caps demand instead of growing supply, and **queue-based load leveling**, which absorbs bursts in a buffer so the consumer fleet can scale more slowly.

## Where

### Where it runs / lives in the stack
It is an **infrastructure control-plane** concern. It sits outside the request path: no request ever passes through an autoscaler. On VMs it is a regional cloud-provider service that calls the compute API. In Kubernetes, HPA runs inside `kube-controller-manager`, VPA and KEDA run as in-cluster operators, and Cluster Autoscaler / Karpenter run as in-cluster controllers that call the cloud provider's node APIs. The application participates only by exposing honest metrics, fast startup, correct readiness probes, and graceful shutdown.

### Where you typically encounter it
- **EC2 Auto Scaling groups** behind an Application Load Balancer, using target tracking on `ASGAverageCPUUtilization` or `ALBRequestCountPerTarget`.
- **Kubernetes HPA** on stateless API Deployments, the most common autoscaler in container platforms.
- **KEDA** on queue consumers (SQS, Kafka, RabbitMQ, Azure Service Bus). Azure Container Apps uses KEDA scale rules as its native scaling model.
- **Karpenter** on EKS (including EKS Auto Mode) and on AKS through Node Auto Provisioning.
- **Knative / Cloud Run** for request-concurrency scaling with scale-to-zero.
- **AWS Application Auto Scaling** for non-EC2 targets: ECS services, DynamoDB capacity, Lambda provisioned concurrency.

### Ecosystem and tooling
- **VM-level autoscalers:** AWS EC2 Auto Scaling, Azure Monitor autoscale for VMSS, GCP MIG autoscaler.
- **Pod replica count:** HPA (`autoscaling/v2`, GA since Kubernetes 1.23), KEDA (CNCF graduated project), Knative Pod Autoscaler (KPA).
- **Pod right-sizing:** VPA (from the `kubernetes/autoscaler` repo), in-place resize via the `/resize` subresource.
- **Node provisioning:** Cluster Autoscaler (node-group based), Karpenter (group-less, `NodePool` + provider-specific node class), GKE node auto-provisioning.
- **Metrics plumbing:** metrics-server, Prometheus Adapter, KEDA's metrics adapter, cloud-native monitoring services.
- **Proactive capacity:** EC2 predictive scaling and warm pools, Azure predictive autoscale, GCP predictive autoscaling, scheduled actions on all three.

## When

### When the topic emerged and why
Before elastic clouds, capacity was a procurement problem: you bought servers for forecast peak plus headroom, and they sat idle most of the day. AWS shipped **Auto Scaling, CloudWatch, and Elastic Load Balancing together on May 17, 2009** ([announcement](https://aws.amazon.com/about-aws/whats-new/2009/05/17/monitoring-auto-scaling-elastic-load-balancing)). The bundling tells you something: autoscaling needs a sensor (metrics) and a way to make new instances useful immediately (a load balancer), or it is pointless. Kubernetes added HPA early in its life. `autoscaling/v2` reached GA in 1.23 and added multiple metrics and the `behavior` block. AWS added the dead-time mitigations later: warm pools (April 2021) and native predictive scaling (May 2021). Karpenter appeared in November 2021 and reached [1.0 in August 2024](https://aws.amazon.com/blogs/containers/announcing-karpenter-1-0). It was built because node-group-based Cluster Autoscaler was slow and rigid about instance types.

### When to use it in a project
Reach for it when:
- Peak-to-trough load ratio is large (roughly 2x or more) and recurs, so fixed peak provisioning wastes real money.
- The workload is stateless or its state is externalized, so a new replica is useful the moment it passes readiness.
- Replica startup time is well below the time it takes load to ramp, so capacity arrives before the spike is over.
- You have a metric that tracks demand roughly linearly per replica: requests per target, queue lag per consumer, concurrency.
- Workers drain a queue, where backlog and latency tolerance make reactive scaling safe.

### When NOT to use it
Avoid it when:
- The workload is stateful with expensive rebalancing (database primaries, Kafka brokers, Elasticsearch data nodes). Each scale event is a data migration.
- Load is flat. A fixed fleet plus reserved or committed-use pricing is cheaper and has fewer moving parts.
- Spikes are shorter than provisioning dead time. Use over-provisioning, warm pools, or scheduled scaling instead.
- The bottleneck is a shared downstream (DB connections, a licensed API). Scaling callers amplifies contention.
- Per-replica cost is dominated by a large fixed overhead (JVM warmup, model load onto a GPU). Scale-in/out churn costs more than it saves unless the cycles are long.

## How

### How it works under the hood

**1. Kubernetes HPA control loop.** Every 15 s (`--horizontal-pod-autoscaler-sync-period`), the controller ([docs](https://kubernetes.io/docs/concepts/workloads/autoscaling/horizontal-pod-autoscale/)):
1. Fetches per-pod metrics. CPU utilization is measured *relative to the container's resource request*. No request means no CPU-based scaling.
2. Excludes pods that are not ready, are still initializing (`--horizontal-pod-autoscaler-cpu-initialization-period`, default 5 min; `--horizontal-pod-autoscaler-initial-readiness-delay`, default 30 s), or are missing metrics.
3. Computes `desiredReplicas = ceil(currentReplicas × currentMetric / targetMetric)`. Example: 4 replicas at 90% CPU with a target of 60% gives `ceil(4 × 1.5) = 6`.
4. Skips the change if the ratio is within the tolerance band (default 0.1, so 0.9–1.1). Per-HPA `tolerance` in `behavior.scaleUp`/`scaleDown` went alpha in 1.33, beta in 1.35, and [GA in 1.37](https://www.kubernetes.dev/resources/keps/4951/).
5. Re-runs the math conservatively for excluded pods. When scaling up, missing pods count as 0% usage. When scaling down, they count as 100%. This dampens the move in either direction.
6. With multiple metrics, computes a recommendation per metric and takes the **maximum**.
7. Applies `behavior`. The default scale-up has a 0 s stabilization window and allows the larger of +100% or +4 pods per 15 s. The default scale-down uses a **300 s stabilization window** (it takes the highest recommendation in the last 5 min) and allows up to −100% per 15 s.
8. Clamps to `[minReplicas, maxReplicas]` and writes the result to `/scale`.

**2. Pod and node layering.** HPA only changes a number. The scheduler then tries to place the new pods. If they stay `Pending` for lack of room, the node layer reacts:

```
 load ↑ → metrics-server scrape (default 60s, often set to 15s)
        → HPA tick (≤15s) → Deployment replicas ↑ → pods Pending
        → Cluster Autoscaler scan (10s) simulates pending pods on node-group templates
          or Karpenter bin-packs pending pods onto a freshly chosen instance type
        → cloud VM boot + kubelet join + image pull → pod Ready → LB adds endpoint
```

Total reaction time is the sum of these lags. A pod-only scale-up can finish in seconds. One that needs a new node commonly takes minutes. Cluster Autoscaler's defaults: scan every 10 s, a node is a scale-down candidate below 50% requested utilization for 10 min, and there is a 10 min delay after a scale-up before any scale-down ([FAQ](https://github.com/kubernetes/autoscaler/blob/master/cluster-autoscaler/FAQ.md)). Karpenter replaces the node groups with direct instance selection and **consolidation**, meaning it replaces or deletes under-used nodes. Its disruption budgets default to 10% of nodes at a time.

**3. VM autoscalers.** AWS target tracking creates two CloudWatch alarms for you. Commonly reported behavior is 3 consecutive 1-minute breaches to scale out and 15 to scale in, which is the same up-fast/down-slow asymmetry. New instances are excluded from the group metric until the **default instance warmup** expires. The legacy **default cooldown is 300 s** and applies only to simple scaling policies ([docs](https://docs.aws.amazon.com/autoscaling/ec2/userguide/ec2-auto-scaling-scaling-cooldowns.html)). GCP MIGs use a 60 s default initialization period and a 10 min scale-in stabilization period that sizes for the *peak* load in that window. Azure Monitor autoscale uses threshold rules with per-rule cooldowns (5 min in Microsoft's examples) and checks whether a planned scale-in would immediately trigger a scale-out before it acts.

**4. Event-driven and scale-to-zero.** KEDA polls each trigger every 30 s by default. It handles the 0↔1 transition itself and hands 1↔N to an HPA it generates. Its `cooldownPeriod` (default 300 s) applies only to the final drop to zero. Core HPA scale-to-zero (`HPAScaleToZero`, object/external metrics only) was alpha from 1.16 and became [beta and on by default in 1.37](https://kubernetes.io/blog/2026/09/02/kubernetes-v1-37-hpa-scale-to-zero-beta/). Knative's KPA scales on in-flight concurrency (target 100 per pod by default). It averages over a 60 s stable window and switches to a 6 s **panic window** when the short-window load hits 200% of target.

**5. Vertical.** VPA's recommender builds histograms of observed usage and proposes requests. Historically the updater *evicted* pods to apply new values. In-place pod resize went [GA in Kubernetes 1.35](https://kubernetes.io/blog/2025/12/19/kubernetes-v1-35-in-place-pod-resize-ga/). VPA's `InPlaceOrRecreate` mode patches `/resize` first and evicts only when the node lacks room.

### Key trade-offs

| Choice | You gain | You give up |
|---|---|---|
| Horizontal over vertical | No restart, fault isolation, effectively unbounded ceiling | Requires statelessness. Per-replica fixed overhead multiplies |
| Utilization metric (CPU) over demand metric (RPS, queue lag) | Zero setup, always available | Indirect. CPU saturates late for I/O-bound services and lies under throttling |
| Low target (e.g. 50%) over high (e.g. 80%) | Headroom that absorbs the spike while new capacity boots | You pay for roughly 30% more idle capacity all the time |
| Fast scale-up, slow scale-in (the default everywhere) | Under-provisioning, which is the expensive error, is short | Paying for capacity for 5–15 min after the peak |
| Reactive over predictive | No training data needed, handles novel spikes | Always one dead-time behind the curve |
| Predictive / scheduled | Capacity ready before the ramp | Needs ≥7–14 days of cyclical history. Wrong on anomalies |
| Node groups (CA) over group-less (Karpenter) | Predictable instance types, simpler quota and governance | Slower, worse bin-packing, one group per shape |
| Scale to zero | Zero idle cost, important for GPUs | Cold-start latency on the first request |

### Common failure modes
- **Flapping.** Replica count oscillates every few minutes. *Cause:* scale-in raises per-replica load past the scale-out threshold. The band between thresholds or the stabilization window is too narrow.
- **HPA does nothing.** `<unknown>/60%` in `kubectl get hpa`. *Cause:* containers lack CPU requests, or metrics-server / the adapter is not installed.
- **Spike outruns capacity.** 5xx errors for minutes, then recovery once the incident is over. *Cause:* the spike is shorter than boot time plus metric lag. No headroom or warm pool.
- **Pods Pending forever.** *Cause:* `maxReplicas` above what node `max` or cloud quota allows. Instance-type stockout. Node selectors no node group satisfies.
- **Dropped requests on scale-in.** *Cause:* no `preStop` delay or connection draining. The pod dies before the LB stops routing to it. `terminationGracePeriodSeconds` (30 s default) is shorter than in-flight work.
- **Scale-out makes it worse.** *Cause:* the shared database hits its connection ceiling. Every new replica opens another pool.
- **Nodes never scale in.** *Cause:* pods with `emptyDir`/local storage, restrictive PDBs, or `safe-to-evict: "false"` pin the node. Cluster Autoscaler skips them.
- **HPA and VPA fight.** *Cause:* both act on CPU for the same workload. VPA raises requests, utilization drops, HPA scales in, and the cycle repeats.
- **Retry storm feedback.** *Cause:* client retries inflate RPS during an outage. The autoscaler scales on self-inflicted load and exhausts `max`.

## Why

### Why it exists
Two first-principles costs pull in opposite directions: **cost of idle capacity** (linear in time × provisioned units) and **cost of insufficient capacity** (latency, errors, lost revenue, which grow non-linearly once queues form). Queueing theory makes the second one steep: as utilization approaches 100%, wait time grows without bound, so you cannot simply run hot. Static provisioning fixes one point on that curve for all hours. Autoscaling moves the operating point with demand. That turns capacity from a capital-planning decision into a runtime control problem. Elastic, per-second-billed cloud infrastructure made it possible.

### Why it looks the way it does
- **Proportional target tracking instead of threshold steps or full PID.** Step rules ("if CPU > 70% add 2") need hand-tuned step sizes per fleet size and oscillate easily. A full PID controller has derivative terms that amplify noisy metrics and integral windup during long boot dead time. The proportional formula `ceil(n × actual/target)` scales the correction with fleet size, and the tolerance band plus stabilization windows replace the damping a PID would provide. *(This is an engineering reading of the design, not a quote from the Kubernetes or AWS docs.)*
- **Asymmetric damping.** Scale-up reacts immediately while scale-in waits 5–15 minutes, because the two errors are asymmetric: an under-provisioned minute produces user-visible errors, while an over-provisioned minute costs cents. Scale-in also destroys state: in-flight requests, caches, warmed JITs.
- **Separate pod and node controllers instead of one global optimizer.** Kubernetes decouples "how many pods" from "how many machines" and links them only through the `Pending` state. The alternative, one controller that plans pods and nodes jointly, could react faster and pack better, but it would couple every workload policy to every cloud's instance catalogue. The decoupled design lets HPA, KEDA, and VPA evolve independently of Cluster Autoscaler or Karpenter, at the cost of stacked latency. Karpenter narrows that gap by provisioning directly from pending-pod requirements instead of from fixed node-group templates.

### Why it matters now
In 2026 the cost pressure has moved to **accelerated compute**. An idle GPU node costs many times more per hour than an idle general-purpose VM, and LLM inference load is bursty. The Kubernetes project has responded within the last year: in-place pod resize GA (1.35, December 2025), per-HPA tolerance GA and HPA scale-to-zero beta-by-default (both 1.37, August 2026). Karpenter-style group-less provisioning is now the managed default on EKS Auto Mode and AKS Node Auto Provisioning. The field is **transforming, not stable**. CPU-based HPA is giving way to demand signals (queue depth, concurrency, tokens in flight) and to combined predictive-plus-reactive policies.

## Open questions / things to verify in practice
- What is the real end-to-end scale-out latency for *my* workload, measured from load step to new pod `Ready` and receiving traffic, with and without a new node? Which stage dominates?
- Does my chosen metric scale linearly with replicas? Double the load at fixed replicas and check whether `actual/target` predicts the needed count.
- On scale-in, are any requests dropped? Run a load test while forcing scale-down and inspect LB 5xx and client errors.
- What does the default 300 s scale-down window cost per day for my traffic shape, and is a per-HPA `tolerance` or shorter window safe?
- Which pods pin nodes and block Cluster Autoscaler / Karpenter scale-in (`emptyDir`, PDBs, annotations)?
- If a dependency fails and clients retry, does the autoscaler run straight to `maxReplicas`, and what does that cost?
