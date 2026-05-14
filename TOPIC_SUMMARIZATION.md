# Circuit Breaker

A circuit breaker is a small wrapper around a network call that stops trying when the service on the other end is clearly broken. It watches the recent failure rate, and once things look bad it flips into an "open" state where every call returns an instant error instead of waiting on a doomed request. After a cool-down it cautiously sends a probe or two to check whether the downstream has recovered, and only then resumes normal traffic.

Engineers reach for it whenever one synchronous dependency can drag a whole service down with it. If a payment API starts taking thirty seconds to respond, every caller keeps a thread and a connection tied up for thirty seconds, the worker pool drains, and a single bad dependency turns into a company-wide outage. The breaker prevents that cascading failure: callers fail fast, the failing service gets breathing room instead of being hammered by retries, and the system as a whole stays responsive even while one piece is sick.

The analogy is the breaker in a house's electrical panel. Under normal load current flows through it and nobody thinks about it; when something downstream shorts, it physically opens the circuit so the fault cannot burn down the wiring upstream. A software circuit breaker does the same job for a remote call — three states (Closed, Open, Half-Open), one trip threshold, one cool-down, and a lot fewer 3am pages.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/backend/circuit-breaker/
