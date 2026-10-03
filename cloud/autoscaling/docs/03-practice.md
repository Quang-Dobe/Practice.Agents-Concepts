# Autoscaling — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic
In a typical SaaS backend, autoscaling is the HPA (or ASG target-tracking policy) attached to the stateless API tier behind the load balancer. Nobody looks at it until a launch day, or until the monthly bill arrives and someone asks why the cluster never shrinks at night.

In anything with background work, such as email sending, video transcoding, or document ingestion, it is the KEDA `ScaledObject` or SQS-backlog policy that decides how many workers drain the queue. This is the easiest place to get autoscaling right, because a backlog is an honest demand signal.

On a Kubernetes platform team, it is the Cluster Autoscaler or Karpenter install that every product team depends on without knowing it exists. Its tuning decides whether a pod scale-up takes 10 seconds or 4 minutes, and whether nodes ever get consolidated. In 2026 the most expensive version of this is GPU inference, where an idle node costs real money every hour.

## Best practices

### 1. Scale on the signal that actually saturates
**Do:** For I/O-bound APIs, scale on requests per target or in-flight concurrency. For workers, scale on queue backlog per replica. Use CPU only for services that are demonstrably CPU-bound.
**Why:** CPU *drops* when threads block on a slow network or database, so a CPU-driven autoscaler removes capacity in the middle of an incident (see Slack below).
**Avoid:** Defaulting to `averageUtilization: 70` on CPU because it is the example in the docs.

### 2. Derive the target from an SLO, not from intuition
**Do:** Compute it. AWS's SQS guidance: acceptable backlog per instance = acceptable latency / per-message processing time. A 10 s latency budget with 0.1 s per message gives a target of 100 messages per instance.
**Why:** A target someone picked by feel is either too hot (queues form during dead time) or too cold (30% of the fleet idles forever), and nobody can explain which.
**Avoid:** Copying 80% CPU from a blog post into a JVM service with a 90-second warmup.

### 3. Set requests on every container, including sidecars
**Do:** Give every container honest CPU and memory requests. Where sidecars distort the average, use `ContainerResource` metrics so the HPA reads only the app container.
**Why:** HPA utilization is measured against requests. If one container has no request, the HPA shows `<unknown>` and silently stops scaling.
**Avoid:** Setting tiny requests "so the HPA scales eagerly". That overpacks nodes, because schedulers and node autoscalers plan by requests.

### 4. Let the autoscaler own the replica count
**Do:** Remove `spec.replicas` from the Deployment manifest once an HPA manages it, as the Kubernetes docs recommend. In GitOps, make sure the tool does not reassert a replica count.
**Why:** Every deploy otherwise resets the Deployment to the manifest value. Ten replicas at peak drop to three for a full HPA cycle in the middle of a rollout.
**Avoid:** Leaving `replicas: 3` in the YAML "as a default".

### 5. Size headroom to your measured dead time
**Do:** Measure load step → new pod `Ready` and receiving traffic, both with and without a new node. If traffic can grow 40% within that window, the target has to leave roughly 40% headroom.
**Why:** Reactive scaling always lags by one dead time. Headroom is the only thing that serves traffic during that lag.
**Avoid:** Tuning targets without ever timing a cold scale-out that includes a new node.

### 6. Treat `maxReplicas` as a blast-radius limit tied to downstream capacity
**Do:** Set the maximum so that `maxReplicas × connection pool size` stays under the database's `max_connections` minus headroom. Alert when the HPA sits at max.
**Why:** 40 replicas × 20 connections is 800 connections, against a PostgreSQL default of 100. Scaling out turns a slow app into a database outage. During a retry storm, max is also your cost ceiling.
**Avoid:** `maxReplicas: 1000` "so it never runs out".

### 7. Make scale-in boring
**Do:** Add a `preStop` sleep of about 5–15 s so the load balancer deregisters the pod before the process stops. Set `terminationGracePeriodSeconds` above your longest request. Add PDBs. On ASGs, use lifecycle hooks plus instance scale-in protection for workers holding jobs.
**Why:** Scale-in is a deployment that happens dozens of times a day. Without draining, every scale-in produces a small burst of 502s that nobody can reproduce.
**Avoid:** Relying on the default 30 s grace period for workers that process 10-minute jobs.

### 8. Scale up fast, scale down slow, and keep the defaults until data says otherwise
**Do:** Start with the platform defaults (HPA's 300 s scale-down window, 0 s scale-up window). Shorten scale-down only after you have priced the idle tail and confirmed it does not cause flapping.
**Why:** An under-provisioned minute shows up as user-visible errors. An over-provisioned minute costs cents. Netflix arrived at the same rule.
**Avoid:** Symmetric windows "for tidiness".

### 9. Cut startup time before tuning the controller
**Do:** Use baked images or AMIs, small container images, lazy initialization off the critical path, and readiness probes that pass only when the app can really serve traffic.
**Why:** Every second removed from startup is a second of dead time no controller setting can recover.
**Avoid:** Readiness that returns 200 before caches and JIT are warm. The pod gets full traffic and times out.

### 10. Pre-scale for events you already know about
**Do:** Raise `minReplicas` or ASG minimum capacity with scheduled actions (or the KEDA cron scaler) before launches, sales, and matches. Use predictive scaling for daily cycles.
**Why:** No reactive loop can absorb growth that is faster than its dead time.
**Avoid:** "The autoscaler will handle Black Friday."

### 11. Keep warm node capacity and diverse instance types
**Do:** Run low-priority pause pods (a negative `PriorityClass`) as overprovisioning, or EC2 warm pools. Give Karpenter NodePools or ASG mixed-instances policies several instance families and zones.
**Why:** Pause pods turn a 3-minute node boot into a 10-second preemption. Instance diversity avoids `InsufficientInstanceCapacity` exactly when everyone in the region is scaling.
**Avoid:** One node group with one instance type in one zone.

### 12. Keep HPA and VPA off the same resource
**Do:** Use VPA in recommendation mode (`updateMode: "Off"`) to right-size requests, and let HPA scale on a demand metric.
**Why:** If both act on CPU, VPA raises requests, utilization drops, HPA scales in, and the loop repeats.
**Avoid:** Turning both on "for full automation".

## Anti-patterns to recognize

- **Scaling on p99 latency**: The HPA targets a latency metric. Latency rises for reasons replicas cannot fix (a slow dependency, a lock, GC), so the fleet runs to max and puts more load on the dependency that caused the problem. Alert on latency, and scale on concurrency or RPS.
- **Counting retries as demand**: The RPS metric includes client retries, health checks, and bot traffic. During a downstream outage, retries inflate RPS and the autoscaler pays to amplify the incident. Scale on successful-request concurrency or backlog, and cap retries with budgets.
- **`minReplicas: 1` on a user-facing service**: It looks thrifty overnight. A single pod is a single point of failure, and the morning ramp starts from zero headroom. Use a floor of 2–3 spread across zones with `topologySpreadConstraints`.
- **Node-pinning pods everywhere**: `emptyDir` scratch space, `safe-to-evict: "false"`, and PDBs with `maxUnavailable: 0` look harmless one at a time. Together they stop Cluster Autoscaler and Karpenter from removing nodes, and the cluster only ever grows. Audit blockers quarterly and use `karpenter.sh/do-not-disrupt` only on pods that truly need it.
- **Autoscaling a stateful tier like a stateless one**: An HPA on Kafka consumers with more replicas than partitions, or on a cache tier. Extra consumers sit idle, and every scale event triggers a rebalance that pauses consumption. Cap replicas at the partition count, or scale the partitions deliberately.
- **Fixing flapping by stretching every window to 30 minutes**: The oscillation stops, and so does the autoscaler. Widen the tolerance band, or fix the metric's non-linearity, instead of hiding the problem behind lag.
- **Never testing scale-out at burst size**: The provisioning path (AMI bootstrap, config service, image registry, cloud quotas) is only ever exercised one instance at a time. It fails when 200 instances start at once. Load-test the scale-up path itself.

## Real-world usage patterns

**Chat SaaS, CPU-scaled web tier (Slack, January 2021).** Slack's web tier scaled on CPU through AWS autoscaling. When an AWS Transit Gateway saturated, threads blocked on the network, CPU fell, and the autoscaler *scaled the web tier down*. The recovery scale-up then overloaded the internal provision-service, which hit the Linux open-files limit and an AWS quota. *Lesson:* the autoscaler's metric and the scale-up machinery are both production dependencies. Test them during a degraded-network game day, not only under clean load.

**Live sports streaming (Hotstar, 25.3M concurrent viewers).** Viewer counts grew by more than 1M per minute around key moments, and Hotstar reported that ASG step scaling, single-instance-type groups, and capacity errors could not keep up. They pre-warmed before matches, scaled with their own script on request count and platform concurrency, used fully baked AMIs to fit a ~90 s reaction window, and used Spot Fleet across instance types. *Lesson:* past a certain growth rate, autoscaling turns into a capacity-scheduling problem, and reactive scaling only handles the residual error.

**Queue-driven workers (document processing, video, email).** KEDA or an SQS target-tracking policy scales on backlog per worker, often down to zero overnight. Workers enable scale-in protection while they hold a job. *Lesson:* scale-to-zero is safe here because the queue absorbs the cold start. The same setting on a synchronous API turns into a user-facing latency spike.

**Streaming video, predictive plus reactive (Netflix Scryer).** Netflix layered a forecast-driven engine over reactive Amazon autoscaling. The forecast sets capacity ahead of the daily ramp, and the reactive policy corrects for deviations. *Lesson:* reactive scaling misreads traffic drops caused by outages. It scales in, then the recovery surge hits a shrunken fleet. A forecast-based floor protects against that.

## Operational checklist

- [ ] **Monitoring:** Are desired vs current vs max replicas, pending-pod age, scale-out latency (load step → `Ready`), and the scaling metric itself all on one dashboard?
- [ ] **Alerting:** Is there an alert for "HPA/ASG at `max` for more than 10 minutes" and for "HPA metric `<unknown>`"?
- [ ] **Signal:** Has someone verified that the metric scales roughly linearly with replicas (double load at fixed replicas, check `actual/target`)?
- [ ] **Failure handling:** Has a load test run while forcing scale-in, with zero LB 5xx? Are `preStop` hooks, grace periods, and PDBs in place?
- [ ] **Dependencies:** Does `maxReplicas × pool size` fit inside the database's connection limit, and the limits of any rate-limited API?
- [ ] **Security:** Is the node autoscaler's IAM role scoped to the specific node groups or launch templates it may create, rather than `ec2:*`? Can a compromised workload inflate its own scaling metric to drive the bill?
- [ ] **Cost:** Do budget alerts fire on node count or spend anomalies? Has anyone audited node-pinning pods that block scale-in?
- [ ] **Capacity:** Do cloud quotas and IP space (subnet CIDR, VPC CNI IPs per node) cover `max` across all autoscaled groups at once?
- [ ] **Onboarding:** Does a new engineer know where the HPA/ScaledObject lives, which metric drives it, and why `spec.replicas` is absent from the manifest?

## How this topic typically evolves in a codebase
Teams start with a fixed fleet, then add a CPU-based HPA or ASG target-tracking policy with default settings, often copied between services. That works until the first incident in which CPU does not track demand: an I/O-bound service fails to scale, or scales in during an outage. The typical next step is custom metrics through Prometheus Adapter or KEDA, plus a node autoscaler once pods start sitting in `Pending`.

The painful migration point usually arrives at the boundary with shared resources. Replica counts grow until database connections run out, which forces a connection pooler (PgBouncer, RDS Proxy) and a hard look at `maxReplicas`. The node layer also starts to dominate latency and cost. Moving from node-group Cluster Autoscaler to Karpenter, or adopting EKS Auto Mode or AKS Node Auto Provisioning, means re-examining every node selector, PDB, and `do-not-disrupt` annotation, because consolidation actively evicts pods that used to stay put.

Mature setups end up with three layers: scheduled or predictive floors for known cycles and events, reactive demand-signal scaling for the deviations, and cost governance on top (spend alerts, quarterly right-sizing from VPA recommendations, a reviewed list of node-pinning exceptions). Autoscaling config then gets the same review discipline as application code, because it changes production capacity many times a day.

## Further reading
- [Kubernetes: Horizontal Pod Autoscaling](https://kubernetes.io/docs/concepts/workloads/autoscaling/horizontal-pod-autoscale/): the authoritative reference for the algorithm, `behavior`, container metrics, and the `spec.replicas` migration note.
- [Cluster Autoscaler FAQ](https://github.com/kubernetes/autoscaler/blob/master/cluster-autoscaler/FAQ.md): explains why nodes do not scale down and how to set up overprovisioning with pause pods. Most node-layer debugging ends here.
- [Karpenter: Disruption](https://karpenter.sh/docs/concepts/disruption/): consolidation, disruption budgets, and `do-not-disrupt`. Read it before turning consolidation on in production.
- [AWS: Scaling policy based on Amazon SQS](https://docs.aws.amazon.com/autoscaling/ec2/userguide/as-using-sqs-queue.html): the backlog-per-instance method for deriving a target from a latency SLO.
- [Slack's Outage on January 4th, 2021](https://slack.engineering/slacks-outage-on-january-4th-2021/): a post-mortem showing CPU-based scaling and the provisioning path failing together.
- [Scryer: Netflix's Predictive Auto Scaling Engine](https://medium.com/netflix-techblog/scryer-netflixs-predictive-auto-scaling-engine-a3f8fc922270): why a mature team layers forecasting on top of reactive scaling.
