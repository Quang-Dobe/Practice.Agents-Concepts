namespace Domain;

// The three canonical states from the deep-dive doc.
// Closed   = traffic flows, failures are counted.
// Open     = calls fail fast, no network touched.
// HalfOpen = a single probe is in flight to test recovery.
public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}
