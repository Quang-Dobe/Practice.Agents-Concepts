namespace Domain;

// Sentinel thrown when the breaker is Open and refuses a call.
// Analogous to Polly's BrokenCircuitException or resilience4j's CallNotPermittedException.
// Callers catch this specifically to take the fallback path.
public sealed class CircuitOpenException(string circuitName)
    : Exception($"Circuit '{circuitName}' is open — call short-circuited.");
