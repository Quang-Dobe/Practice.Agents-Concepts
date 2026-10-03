// Autoscaling MVP: a tick-by-tick simulation of the Kubernetes HPA control loop.
//
// What this file proves (see ../docs/02-deep-dive.md § How, "Kubernetes HPA control loop"):
//   1. The proportional rule  desired = ceil(current * actual / target), skipped inside a 10% tolerance band.
//   2. Dead time: new pods need WarmupTicks before they serve traffic, so a spike drops requests briefly.
//   3. The conservative re-run: warming pods count as 0% so the loop doesn't re-order capacity already on its way.
//   4. Asymmetric damping: scale-up acts at once (rate-limited), while scale-down waits on a 300 s
//      stabilization window and sizes for the *highest* recommendation in that window.
// No cluster or cloud account is needed. The "fleet" is a list of integers.

const int TickSeconds = 15;                            // HPA sync period (--horizontal-pod-autoscaler-sync-period)
const double PodCapacityRps = 100;                     // one pod at 100% of its CPU request serves 100 rps
const double Target = 0.60;                            // target average utilization: 60%, leaving 40% headroom
const double Tolerance = 0.10;                         // HPA default: ignore ratios between 0.9 and 1.1
const int MinReplicas = 2, MaxReplicas = 12;           // the only hard guarantees an autoscaler gives you
const int WarmupTicks = 3;                             // 45 s of dead time: boot + image pull + readiness probe
const int ScaleDownWindowTicks = 300 / TickSeconds;    // HPA default scale-down stabilization window: 300 s
const int TotalTicks = 36;                             // 9 minutes of simulated time

// Traffic: quiet, then a step spike at t=60s, then quiet again with a small bump every minute.
static int LoadAt(int tick) => tick switch
{
    < 4 => 200,
    < 12 => 600,
    _ => tick % 4 == 0 ? 380 : 200,
};

// Each entry is the tick at which that pod becomes Ready. Start with 4 pods that are already warm.
List<int> pods = [0, 0, 0, 0];
List<int> recommendations = [];                        // raw recommendation history, one per tick

for (var tick = 0; tick < TotalTicks; tick++)
{
    // --- Sensor: what the metrics pipeline reports this tick ---
    var load = LoadAt(tick);
    var current = pods.Count;
    var ready = pods.Count(readyAt => readyAt <= tick);  // only Ready pods receive traffic from the LB
    var util = load / (ready * PodCapacityRps);           // average utilization the Ready pods feel
    var dropped = Math.Max(0, load - ready * PodCapacityRps); // demand beyond Ready capacity is lost

    // --- Control law: the proportional HPA formula ---
    var ratio = util / Target;
    // Conservative re-run: when scaling up, warming pods count as 0% usage. Without this line the loop
    // sees the same overload again and orders more pods for a spike it already answered.
    if (ratio > 1 && ready < current) ratio = Math.Max(1, load / (current * PodCapacityRps) / Target);
    var rec = Math.Abs(ratio - 1) <= Tolerance ? current : (int)Math.Ceiling(current * ratio);
    recommendations.Add(rec);

    // --- Damping: scale up on the newest recommendation; scale down only to the highest
    //     recommendation seen in the last 300 s. That gap between the two directions is what stops flapping. ---
    var downCeiling = recommendations.TakeLast(ScaleDownWindowTicks).Max();
    var desired = current;
    if (rec > current) desired = rec;
    else if (downCeiling < current) desired = downCeiling;

    // --- Rate limit + bounds: default scale-up allows the larger of +100% or +4 pods per 15 s ---
    desired = Math.Min(desired, Math.Max(current * 2, current + 4));
    desired = Math.Clamp(desired, MinReplicas, MaxReplicas);

    // --- Actuator: create pods (they start warming) or delete pods (newest / still-warming first) ---
    if (desired > current) pods.AddRange(Enumerable.Repeat(tick + WarmupTicks, desired - current));
    if (desired < current) { pods.Sort(); pods.RemoveRange(desired, current - desired); }

    var note = desired > current ? (rec > desired ? "scale UP (rate-limited)" : "scale UP")
             : desired < current ? "scale DOWN"
             : rec < current ? $"hold: window still remembers {downCeiling}"
             : "";
    Console.WriteLine(
        $"t={tick * TickSeconds,3}s load={load,3}rps pods={ready,2} ready +{current - ready} warming " +
        $"util={util * 100,4:F0}% dropped={dropped,3} rec={rec,2} -> {desired,2}  {note}");
}
