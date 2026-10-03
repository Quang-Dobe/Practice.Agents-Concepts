# Autoscaling — MVP Code

The smallest runnable demo of autoscaling: a tick-by-tick simulation of the Kubernetes HPA control loop on a fake fleet. It has about 50 lines of actual code, not counting comments. You don't need a cluster or a cloud account.

## What it demonstrates
- The proportional rule `ceil(current × actual / target)` with the 0.9–1.1 tolerance band (see `../docs/02-deep-dive.md § How`).
- Dead time. New pods warm up for 45 s, so the spike at t=60s drops requests until they turn Ready.
- The conservative re-run. Pods that are still warming count as 0% usage, so the loop doesn't order the same capacity twice.
- Asymmetric damping. Scale-up acts at once, rate-limited to +100% or +4 pods per tick. Scale-down waits out a 300 s window and sizes for the highest recommendation in it.

## Prerequisites
- .NET SDK 8.0 or newer (`dotnet --version`).
- No packages or services.

## Run it
```bash
cd cloud/autoscaling/code
dotnet run
```

## Expected output
```
t= 60s load=600rps pods= 4 ready +0 warming util= 150% dropped=200 rec=10 ->  8  scale UP (rate-limited)
t= 75s load=600rps pods= 4 ready +4 warming util= 150% dropped=200 rec=10 -> 10  scale UP
t=105s load=600rps pods= 8 ready +2 warming util=  75% dropped=  0 rec=10 -> 10
t=120s load=600rps pods=10 ready +0 warming util=  60% dropped=  0 rec=10 -> 10
t=195s load=200rps pods=10 ready +0 warming util=  20% dropped=  0 rec= 4 -> 10  hold: window still remembers 10
t=465s load=200rps pods=10 ready +0 warming util=  20% dropped=  0 rec= 4 ->  7  scale DOWN
t=495s load=200rps pods= 7 ready +0 warming util=  29% dropped=  0 rec= 4 ->  7  hold: window still remembers 7
```
The fleet settles at 7 pods, not 4, because the 380 rps bump every minute keeps a 7 inside the window.

## What to try next
- Set `ScaleDownWindowTicks = 1` to effectively disable the window. The fleet then flaps between 4 and 7 pods every minute.
- Delete the `if (ratio > 1 && ready < current)` line. At t=75s the recommendation jumps to 20, and the fleet overshoots to `MaxReplicas`.
- Set `WarmupTicks = 8` (2 min boot) and count how many ticks drop requests.
- Set `Target = 0.9` to keep less headroom, then compare the steady pod count with the dropped requests during the spike.
