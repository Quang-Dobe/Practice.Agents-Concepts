# Autoscaling — Overview

> Autoscaling is a control loop that watches a load signal (CPU, queue depth, requests per second) and adds or removes compute capacity on its own, so you pay for what the traffic needs instead of what the worst hour needs. Here "autoscaling" means cloud and Kubernetes compute scaling, not database storage auto-growth.

## The 30-second version
Traffic is never flat. It is quiet at 3 a.m. and busy at 9 a.m., and it spikes when marketing sends an email. If you provision for the peak, you pay for idle machines most of the day. If you provision for the average, the site falls over at the peak. Autoscaling closes that gap: a controller measures load every few seconds, compares it to a target you set ("keep average CPU near 60%"), and changes the number or size of instances to stay near that target. The hard part is choosing the right signal and stopping the loop from overreacting.

## The mental model
Picture a supermarket with a manager watching the checkout lines.

- **The signal** is line length. The manager doesn't guess, they look.
- **The target** is the rule: "no more than three people per lane."
- **Scale out** means the lines get long, so the manager calls staff from the back room to open more lanes.
- **Scale in** means it's quiet, so lanes close and staff go home (and stop being paid).
- **Warm-up lag** is the catch. A new cashier needs a couple of minutes to log in and count the drawer, so a sudden rush still hurts briefly.
- **Cooldown** keeps the manager from opening and closing the same lane every 30 seconds just because the line wobbles between two and four people.

That loop is autoscaling: **measure → compare to target → adjust → wait → repeat.**

```
   ┌──────────── metric (CPU, queue depth, RPS) ◄──────────┐
   ▼                                                        │
[Autoscaler] ── desired count ──► [Instances / Pods] ── serve traffic
   ▲                                                        
   └── target + min/max bounds + cooldown (your policy)     
```

Hiring more cashiers is **horizontal** scaling (more copies). Giving one cashier a faster scanner is **vertical** scaling (a bigger machine). Most cloud autoscaling is horizontal, because adding copies doesn't need a restart.

## What it is NOT
- **Not load balancing.** A load balancer spreads traffic across the instances you already have. Autoscaling decides how many instances exist.
- **Not high availability by itself.** Spreading replicas across zones protects you from a failure. Autoscaling only reacts to load, and it does so after a delay.
- **Not instant.** A new VM can take minutes to boot. A new pod can take seconds, or minutes if a new node has to come up first.
- **Not a fix for a slow app.** If one request holds a database lock, ten more replicas just wait on the same lock.

## When you would reach for it
- Traffic follows a daily or weekly pattern and you are paying for peak capacity around the clock.
- Workers pull from a queue (SQS, Kafka, RabbitMQ) and the backlog grows and shrinks.
- You run stateless web or API services behind a load balancer.
- Unpredictable spikes (a launch, a viral post) would otherwise mean someone gets paged to add servers by hand.
- A Kubernetes cluster where pods come and go and the nodes underneath should follow them.

## When you would NOT reach for it
- Stateful systems where a new node needs a large data rebalance (a primary database, a Kafka broker). Scaling these takes planning, not a reflex.
- Load is flat and predictable, so a fixed fleet is simpler and just as cheap.
- Your startup time is longer than your spikes. The spike is over before capacity arrives, so pre-warm or schedule capacity instead.
- A downstream dependency has a hard ceiling (a licensed API, a database connection limit). More callers just move the failure downstream.

## Key vocabulary (just enough to keep reading)
- **Horizontal / vertical scaling.** Horizontal changes the number of instances. Vertical changes the size of each one.
- **Target tracking.** A policy that holds a metric near a set value, like a thermostat.
- **Min / max / desired capacity.** The floor, the ceiling, and the count the autoscaler wants right now.
- **Cooldown / stabilization window.** A wait period that prevents flapping, which means scaling up and down over and over.
- **Reactive vs predictive / scheduled.** Reactive responds to current metrics. Predictive and scheduled scaling add capacity before an expected load.
- **Auto Scaling Group (ASG).** AWS's managed group of EC2 instances that scales as a unit.
- **HPA / VPA.** Kubernetes Horizontal and Vertical Pod Autoscalers. HPA changes the replica count. VPA changes CPU and memory requests.
- **Cluster Autoscaler / Karpenter.** Node-level autoscalers that add machines when pods can't be scheduled.
- **KEDA.** A Kubernetes event-driven autoscaler that scales on outside signals like queue length and can scale down to zero.

## What's next
`02-deep-dive.md` answers What / Where / When / How / Why in detail. It covers how the HPA replica formula works, how pod scaling and node scaling stack on top of each other, why scale-in is the more dangerous direction, and how AWS, Azure, and GCP handle the same loop.
