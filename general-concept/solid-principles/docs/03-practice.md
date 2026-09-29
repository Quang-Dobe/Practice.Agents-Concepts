# SOLID Principles — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend built on ASP.NET Core, Spring, or NestJS, SOLID lives at the seam between your business rules and everything they touch: the database, the payment provider, the email service, the clock. The DI container in `Program.cs` or your `@Configuration` classes shows every DIP decision the team has made. When you ask "why does this handler take six interfaces in its constructor?", you are asking a SOLID question.

It also shows up in code review, usually as vocabulary rather than as a checklist. "This class is doing too much" means SRP. "Why is there an `IUserService` with one implementation?" is the pushback against DIP applied by rule. "This subclass throws on `Save()`" is LSP. In practice most review arguments about SOLID are arguments about **where the seams go**, not whether the principles are true.

The third place is the long-lived monolith that a company wants to split. Whether extracting the billing module takes two weeks or two quarters depends almost entirely on whether billing's dependencies already point inward through narrow, domain-owned interfaces (see `02-deep-dive.md § How`).

## Best practices

### 1. Put interfaces at I/O boundaries, not around every class
**Do:** Create an abstraction where the code crosses a process boundary or touches something nondeterministic: database, HTTP client, message bus, file system, clock, random, environment.
**Why:** Those are the things that make tests slow and flaky and the things you actually swap (a new payment provider, an in-memory fake). Pure domain classes have no such cost; wrapping them doubles file count and makes "Go to definition" land on an interface.
**Avoid:** Generating `IFooService` for every `FooService` because the template or linter expects it.

### 2. Wait for the second variant before building the extension point
**Do:** Write the concrete thing first. Extract a `PaymentGateway` interface when the second provider is real (signed contract, scheduled ticket), and shape it from both implementations.
**Why:** An interface designed from one implementation encodes that implementation's quirks. When PayPal arrives, the "abstraction" returns Stripe-specific fields and every caller changes anyway, which is the wrong-seam failure from `02-deep-dive.md`.
**Avoid:** Speculative OCP for "future flexibility" nobody has asked for.

### 3. Find SRP seams in git history, not in your head
**Do:** Run `git log --format='%an' -- path/to/File.cs | sort | uniq -c` and read the ticket prefixes. If commits come from finance, fulfilment, and marketing tickets, that file has three actors and is a candidate to split along those lines.
**Why:** Actor-based splits reduce merge conflicts and cross-team regressions, which is the production cost SRP targets. Splits based on "this feels big" produce many small classes that still change together.
**Avoid:** Splitting by line count or by method, which yields cohesion-free fragments.

### 4. Let the domain own its ports
**Do:** Place `PaymentGateway` in the domain/application project and implement it in the infrastructure project. Return domain types (`ChargeResult`), never SDK types (`Stripe.Charge`).
**Why:** If the interface lives next to the SDK, upgrading the SDK forces a domain rebuild and redeploy, and the domain still cannot compile without infrastructure. This is the difference between DIP and "layering with interfaces."
**Avoid:** An `Infrastructure.Contracts` package that the domain references.

### 5. Enforce dependency direction with an architecture test
**Do:** Add one test that fails the build when the domain imports infrastructure: NetArchTest or ArchUnitNET in .NET, ArchUnit in Java, `import-linter` in Python, `dependency-cruiser` or Nx module boundaries in TypeScript.
**Why:** DIP erodes one convenient `using` at a time, usually under deadline. Nobody notices in review; the test catches it in CI. Without it, a "clean architecture" repo is typically violated within months.
**Avoid:** Relying on folder names and a wiki page to hold the boundary.

### 6. Run one contract-test suite against every implementation
**Do:** Write an abstract test class for `PaymentGateway` (declined card, zero amount, idempotent retry, timeout behavior) and inherit it for the real adapter, the sandbox adapter, and the in-memory fake.
**Why:** LSP violations in production look like "works in tests, fails in prod," because the fake accepts inputs or never raises errors the real adapter does. A shared suite forces the fake to be honest.
**Avoid:** Hand-written mocks per test that encode whatever the author assumed the real service does.

### 7. Wire everything in one composition root
**Do:** Construct concrete classes only at startup (`Program.cs`, `main`, the DI registration module). Classes receive dependencies through constructors.
**Why:** Hidden `container.Resolve<T>()` calls or `new SqlConnection()` deep in a handler make dependencies invisible, break testability, and turn missing registrations into runtime errors on the first request that hits that path instead of at startup.
**Avoid:** Injecting `IServiceProvider` into business classes.

### 8. Validate container lifetimes at startup
**Do:** Keep ASP.NET Core's scope validation and build-time validation on (`ValidateScopes`, `ValidateOnBuild`; enabled by default in the Development environment) and add a startup test that resolves every registered root in CI.
**Why:** DIP-heavy code moves wiring errors from compile time to run time. The classic incident is a captive dependency: a scoped `DbContext` injected into a singleton, which then shares one context across concurrent requests and throws threading exceptions under load.
**Avoid:** Discovering a missing or mis-scoped registration from a production 500.

### 9. Prefer a function parameter over a one-method interface
**Do:** In Python, TypeScript, Kotlin, or C#, accept `Callable[[], datetime]`, `() => Date`, or `Func<DateTime>` when the dependency is a single operation.
**Why:** You get the OCP/DIP benefit (swappable, testable) without an interface file, an implementing class, and a container registration. Less ceremony means new engineers actually follow the pattern.
**Avoid:** An `IClock` + `SystemClock` + registration trio in a codebase that already passes lambdas everywhere else. (In .NET 8+, `TimeProvider` exists for exactly this.)

### 10. Split interfaces by consumer, not by implementer
**Do:** When the checkout path needs `Charge` and the back-office tool needs `Refund` and `ListDisputes`, define `IPaymentCharger` and `IPaymentAdmin`. One adapter class can implement both.
**Why:** Consumers of the narrow interface do not rebuild or redeploy when admin signatures change, and their fakes are two lines instead of forty stubbed methods.
**Avoid:** A 40-method `IRepository<T>` shared across every service in a NuGet or npm package.

## Anti-patterns to recognize

- **Interface mirroring**: Every class has an identically named interface with one implementer, generated by habit. It doubles navigation cost and gives the illusion of decoupling while every signature change still touches both files. Keep interfaces only at the boundaries in practice #1 and delete the rest; mocking frameworks can mock concrete classes if that was the only reason.
- **The generic repository over an ORM**: `IRepository<T>` with `GetAll`, `Find(Expression<...>)`, `Add`, `Update` wrapped around Entity Framework or Hibernate. It leaks `IQueryable` (so the "abstraction" still depends on the ORM), hides the ORM's useful features, and invites N+1 queries. Define intent-named ports instead (`IOrderReader.GetOpenOrdersFor(customerId)`).
- **Refused bequest for reuse**: `ReadOnlyDocumentStore : DocumentStore` overrides `Save()` to throw `NotSupportedException`. Callers holding a `DocumentStore` crash at runtime, and someone eventually adds `if (store is ReadOnlyDocumentStore)`. Use composition, or split into `IDocumentReader` and `IDocumentWriter`.
- **Test-induced interfaces on pure logic**: Extracting `IPriceCalculator` so a handler's test can mock the calculator. The test now verifies that a mock was called, not that prices are correct, and passes while production computes wrong totals. Use the real pure class in tests; fake only I/O.
- **Strategy for a two-branch `if`**: A `switch` on three stable shipping types becomes an interface, three classes, a factory, and a registry. The code is now closed for new *operations* (adding `EstimateCO2` edits every class) and nobody asked for new types. Keep the `switch` or pattern match until variants actually multiply.
- **Service locator in disguise**: Handlers take `IServiceProvider` or a static `Locator.Get<T>()`. Dependencies vanish from constructors, so reviewers cannot see what a class touches and tests fail with "service not registered." Constructor-inject what the class needs; if the list is ten items long, that is an SRP signal, not a reason to hide it.
- **Onion architecture for a CRUD app**: Four projects (Domain, Application, Infrastructure, Api) for an admin tool with no business rules beyond validation. Every feature touches four folders and a mapper. Start with vertical slices and extract a domain layer when real rules appear.

## Real-world usage patterns

- **Payments at a mid-size e-commerce platform.** Checkout depends on a domain-owned `PaymentGateway`; Stripe, Adyen, and a regional provider implement it and pass a shared contract suite. The non-obvious lesson: the axis that actually varied was not the provider but **failure semantics** (sync decline vs async webhook confirmation). The interface had to be redesigned around a `PaymentAttempt` state machine, because the first version assumed `charge()` returned a final answer.

- **Modular monolith preparing a service extraction.** A logistics company's monolith had a `Notifications` module called directly from twelve places. They introduced a domain-owned `INotifier` port and an architecture test banning direct imports, then extracted the module to a service six months later by swapping one adapter. The lesson: the architecture test did most of the work. The interface alone had been bypassed in three places before the test existed.

- **Plugin host in developer tooling.** An internal build tool exposes a `Step` interface; teams ship steps as packages. OCP here is strict: the host never changes for a new step. The lesson: public extension interfaces are almost impossible to change later, so they were kept to two methods plus a versioned options bag. ISP at a public boundary is a compatibility strategy, not a style preference.

- **Legacy "god service" in a B2B SaaS.** A 6,000-line `AccountService` edited by billing, identity, and provisioning teams caused weekly merge conflicts and regressions. Git history identified three actors; the split followed those lines, with a thin facade kept for old callers. The lesson: they did not introduce a single new interface during the split. SRP was satisfied by moving code, and DIP was applied only where the new modules touched the database.

## Operational checklist

- Does an architecture test fail CI when domain or application code imports infrastructure, ORM, or SDK namespaces?
- Does every port return domain types only, with no SDK or ORM type in any signature?
- Does each port with multiple implementations (including the test fake) run a shared contract-test suite?
- Does the app fail at startup, not on first request, when a registration is missing or a scoped service is captured by a singleton?
- Is there a composition root, with no `IServiceProvider` or locator calls inside business classes?
- For each interface added in this PR: is there a second implementation today, or does it wrap I/O? If neither, why does it exist?
- Is the largest file in the repo edited by more than one team? Is there a ticket to split it by actor?
- Are shared-library interfaces narrow enough that a signature change does not force redeploys of unrelated consumers?
- Can a new engineer find, in the README or `Program.cs`, where dependencies are wired and which layer may reference which?

## How this topic typically evolves in a codebase

Teams usually start in one of two places. Small teams start with no seams: controllers call the ORM and the Stripe SDK directly. This is fine until the first second provider or the first test suite that needs a database, at which point the first real port appears. Teams following a template start at the opposite extreme: Clean Architecture folders and an interface for every class from day one, before anyone knows which parts vary.

Both converge on the same painful moment, usually around the point the product finds its shape and a second team joins. The seam-less codebase has a god service with several actors and no tests that run without infrastructure. The over-abstracted codebase has a hundred single-implementer interfaces, and the change that actually arrives (multi-currency, multi-tenant) cuts across all of them. The migration is the same in both cases: find the real actors and the real axes of change from history, then add or delete abstractions to match. Deleting is harder politically, because it looks like undoing "good design."

Mature codebases end up with fewer interfaces than the template version and more than the naive one: ports at every I/O boundary, narrow consumer-specific interfaces at module boundaries, architecture tests guarding direction, and plain concrete classes or functions everywhere else. At that stage SOLID stops being a review argument and becomes a property enforced by CI, which is the point where it pays for itself.

## Further reading

- [Robert C. Martin, "The Single Responsibility Principle" (2014)](https://blog.cleancoder.com/uncle-bob/2014/05/08/SingleReponsibilityPrinciple.html) — the author's own correction of the "one thing" misreading; short and settles most SRP review arguments.
- [Mark Seemann, "Composition Root"](https://blog.ploeh.dk/2011/07/28/CompositionRoot/) and ["Service Locator is an Anti-Pattern"](https://blog.ploeh.dk/2010/02/03/ServiceLocatorisanAnti-Pattern/) — the clearest writing on making DIP work operationally; his book *Dependency Injection Principles, Practices, and Patterns* expands both.
- [Sandi Metz, "The Wrong Abstraction"](https://sandimetz.com/blog/2016/1/20/the-wrong-abstraction) — why premature OCP costs more than duplication, and how to back out of a bad abstraction.
- [Dan North, "CUPID — for joyful coding"](https://dannorth.net/cupid-for-joyful-coding/) — the strongest current critique of SOLID-as-rules; read it to argue both sides in review.
- [Liskov & Wing, "A Behavioral Notion of Subtyping" (1994)](https://dl.acm.org/doi/10.1145/197320.197383) — the precise LSP rules behind practice #6's contract tests.
