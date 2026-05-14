using Domain;

namespace Infrastructure.Resilience;

// Hand-rolled circuit breaker. Count-based sliding window (last N outcomes),
// failure-ratio threshold + minimum throughput floor, single-permit half-open
// recovery. Mirrors the resilience4j / Polly v8 semantics from 02-deep-dive.md,
// minus production niceties (slow-call detection, jittered backoff, metrics).
//
// State transitions:
//   Closed   -> Open      when failureRatio over window >= threshold AND samples >= minThroughput
//   Open     -> HalfOpen  when breakDuration has elapsed since opening (lazily, on next call)
//   HalfOpen -> Closed    on a successful probe
//   HalfOpen -> Open      on a failed probe
//
// Concurrency: a single lock around state mutation. Demo-grade — production
// implementations use lock-free state with Interlocked.CompareExchange.
public sealed class CircuitBreaker(
    string name,
    int windowSize,
    int minimumThroughput,
    double failureRatioThreshold,
    TimeSpan breakDuration,
    Action<CircuitState, CircuitState, string>? onTransition = null)
{
    // The sliding window of recent outcomes. true = success, false = failure.
    // A queue gives us O(1) append + O(1) evict — that's all we need.
    private readonly Queue<bool> _window = new(windowSize);

    private readonly object _gate = new();
    private CircuitState _state = CircuitState.Closed;
    private DateTimeOffset _openedAt;
    private bool _halfOpenProbeInFlight;

    public CircuitState State { get { lock (_gate) return _state; } }

    // The single entry point. Equivalent to Polly's pipeline.ExecuteAsync(...).
    public async Task<T> Execute<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        // ----- 1. Pre-check: should we even attempt this call? -----
        lock (_gate)
        {
            if (_state == CircuitState.Open)
            {
                // Has the cool-down elapsed? If so, promote to HalfOpen and
                // admit *this* caller as the single probe. Other callers in
                // the same window will see HalfOpen-with-probe-in-flight and
                // be rejected — that's the thundering-herd guard from the deep-dive.
                if (DateTimeOffset.UtcNow - _openedAt >= breakDuration)
                    Transition(CircuitState.HalfOpen, "break duration elapsed");
                else
                    throw new CircuitOpenException(name);
            }

            if (_state == CircuitState.HalfOpen)
            {
                if (_halfOpenProbeInFlight)
                    throw new CircuitOpenException(name); // already probing, reject extra concurrent callers
                _halfOpenProbeInFlight = true;            // this caller becomes THE probe
            }
        }

        // ----- 2. Forward the call. Stopwatch / slow-call detection omitted for clarity. -----
        try
        {
            var result = await operation(ct);
            RecordOutcome(success: true);
            return result;
        }
        catch
        {
            RecordOutcome(success: false);
            throw;
        }
    }

    // ----- 3. Window update + threshold evaluation. -----
    private void RecordOutcome(bool success)
    {
        lock (_gate)
        {
            // A probe result decides Half-Open -> Closed (success) or Half-Open -> Open (fail).
            // Notice we don't pollute the Closed-state window with probe outcomes; on success
            // we reset it, on failure we restart the break timer.
            if (_state == CircuitState.HalfOpen)
            {
                _halfOpenProbeInFlight = false;
                if (success)
                {
                    _window.Clear();
                    Transition(CircuitState.Closed, "probe succeeded");
                }
                else
                {
                    _openedAt = DateTimeOffset.UtcNow;
                    Transition(CircuitState.Open, "probe failed");
                }
                return;
            }

            // Closed-state bookkeeping: append to the ring buffer, then check the ratio.
            if (_window.Count == windowSize) _window.Dequeue();
            _window.Enqueue(success);

            // The minimum-throughput floor is what stops a single failure out of one
            // call from tripping the breaker. Without it, low-traffic services flap.
            if (_window.Count < minimumThroughput) return;

            var failures = _window.Count(r => !r);
            var ratio = (double)failures / _window.Count;
            if (ratio >= failureRatioThreshold)
            {
                _openedAt = DateTimeOffset.UtcNow;
                Transition(CircuitState.Open,
                    $"failure ratio {ratio:P0} >= {failureRatioThreshold:P0} over {_window.Count} samples");
            }
        }
    }

    // All state changes funnel here so the demo can print every transition.
    private void Transition(CircuitState next, string reason)
    {
        var prev = _state;
        _state = next;
        onTransition?.Invoke(prev, next, reason);
    }
}
