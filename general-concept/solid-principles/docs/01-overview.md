# SOLID Principles — Overview

> SOLID is five object-oriented design rules of thumb that keep code cheap to change by controlling what depends on what.

## The 30-second version

SOLID is a set of five principles, collected by Robert C. Martin ("Uncle Bob") and later packed into an acronym by Michael Feathers. They are not about making code work. They are about making code **survive change**: a new requirement should mean adding or editing a small piece in one place, not surgery across twenty files. Every principle is a different angle on one idea: manage dependencies so a change in one part does not ripple into others. If you have ever made a "one-line fix" that broke three unrelated features, you have felt the problem SOLID addresses.

## The mental model

Think of the electrical system in a house.

- **S — Single Responsibility.** Each circuit breaker covers one area. When the kitchen trips, the bedroom lights stay on. In code, a class answers to one group of people, so the accountant's rule change never touches the code the warehouse depends on.
- **O — Open/Closed.** You add a new appliance by plugging it into an outlet, not by opening the wall and rewiring. Code should let you add behavior (a new payment method) by writing new code, not by editing working code.
- **L — Liskov Substitution.** Anything with a standard plug must behave like a normal appliance when plugged in. A "lamp" that shorts the whole house breaks the promise. A subclass must honor everything its parent promised, or callers break in ways they cannot predict.
- **I — Interface Segregation.** A toaster plugs into a two-prong socket, not a 50-pin industrial connector. Don't force a client to depend on methods it never calls.
- **D — Dependency Inversion.** Your lamp depends on the outlet standard, not on a specific power plant. The utility company also conforms to that standard. Both sides depend on the agreed contract, so you can swap the power source without touching the lamp. In code, business logic depends on an interface such as `PaymentGateway`, not on `StripeClient` directly.

```
   OrderService ──► «PaymentGateway» ◄── StripeGateway
   (high level)       (the outlet)        (low level)
```

The outlet is the whole trick. Four of the five principles are really about putting a good outlet in the right place.

## What it is NOT

- Not a law. SOLID is a set of heuristics. Applied blindly, it produces its own mess.
- Not Dependency Injection. DI is a *technique* (pass dependencies in from outside). DIP is the *principle* that says which direction dependencies should point.
- Not "one method per class." SRP is about reasons to change (who asks for changes), not about line counts.
- Not design patterns. Patterns like Strategy or Adapter are concrete recipes. SOLID is the reasoning behind many of them.
- Not OOP-only in spirit. The vocabulary is class-based, but the ideas carry over to modules, services, and functions.

## When you would reach for it

- A class keeps changing for unrelated reasons, and each change causes merge conflicts between teams.
- You add a new variant (a report format, a shipping carrier) by adding another `if/else` branch to the same function every time.
- Unit testing a class requires a real database, network, or clock because it creates them internally.
- A subclass overrides a method to throw `NotImplementedException`, and callers have to type-check to avoid it.
- You are designing the boundary between business rules and infrastructure in a codebase expected to live for years.

## When you would NOT reach for it

- Throwaway scripts, spikes, and prototypes. The change you are protecting against will never come.
- When there is only one implementation and no real prospect of a second. An interface with one implementer is often just indirection.
- Early in a design, before you know which parts will actually vary. Guessing wrong puts the outlets in the wrong walls.
- When the principle becomes the goal. Ten tiny classes to print a greeting cost more to read than one plain function.

## Key vocabulary (just enough to keep reading)

- **Coupling** — how much one piece of code must know about another in order to work.
- **Cohesion** — how closely the contents of one module belong together.
- **Abstraction** — an interface or base type that describes *what* without committing to *how*.
- **Actor** — the person or group whose requests cause a module to change. SRP's unit of "responsibility."
- **Contract** — the promises a type makes: its inputs, outputs, and guarantees. LSP is about keeping them.
- **High-level vs low-level module** — business rules versus the plumbing (databases, HTTP, files) they use.
- **Dependency Injection** — handing a class its collaborators instead of letting it create them.
- **Extension point** — a deliberate spot where new behavior plugs in without editing existing code.

## What's next

The next document answers What / Where / When / How / Why in detail. It takes each of the five principles in turn, shows the violation and the fix side by side, and explains where the principles pull against each other.
