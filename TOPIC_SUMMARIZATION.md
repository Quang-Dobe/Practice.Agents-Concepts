# SOLID Principles

SOLID is a set of five object-oriented design rules of thumb, collected by Robert C. Martin and named by Michael Feathers: Single Responsibility, Open/Closed, Liskov Substitution, Interface Segregation, and Dependency Inversion. They are not about making code work. They are about making code survive change, by controlling what depends on what so that a change in one part does not ripple into others.

It matters once a codebase has to live for years. Engineers reach for SOLID when a class keeps changing for unrelated reasons, when every new variant means another if/else branch in the same function, when unit tests need a real database or network because a class creates them internally, or when a subclass throws "not implemented" and callers must type-check around it. It is a set of heuristics, not a law: for throwaway scripts, or when only one implementation will ever exist, it mostly adds indirection.

Think of the electrical system in a house. Each circuit breaker covers one area, so a kitchen fault leaves the bedroom lit. You add an appliance by plugging it into an outlet, not by rewiring the wall. Any plug-in device must behave like a normal appliance, a toaster needs a two-prong socket rather than a 50-pin connector, and your lamp depends on the outlet standard rather than on a specific power plant. In code, the outlet is an interface such as PaymentGateway: the order service depends on it, and a Stripe gateway plugs into it, so either side can change without touching the other.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/general-concept/solid-principles/present/index.html
