# SOLID Principles — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

SOLID is a set of five module-level design heuristics for managing **source-code dependencies** in object-oriented (and, by extension, modular) systems. Their shared goal is to minimize the **change-propagation cost**: the number of modules you must edit, recompile, retest, and redeploy when one requirement changes. Four of them (O, L, I, D) govern how abstractions sit between modules; S governs how you cut modules in the first place.

### The core building blocks

- **SRP — Single Responsibility Principle.** "A module should be responsible to one, and only one, actor." Martin's original phrasing was "one reason to change"; his [2014 clarification](https://blog.cleancoder.com/uncle-bob/2014/05/08/SingleReponsibilityPrinciple.html) states that "the reasons for change are people" and restates it as: gather together things that change for the same reasons, separate things that change for different reasons. It is a cohesion rule, not a size rule.
- **OCP — Open/Closed Principle.** "Software entities should be open for extension, but closed for modification." Coined by Bertrand Meyer in *Object-Oriented Software Construction* (1988), where extension meant subclassing. Martin's version (the one used today) is **polymorphic OCP**: callers depend on an abstract interface, and new behavior arrives as a new implementation.
- **LSP — Liskov Substitution Principle.** From Barbara Liskov's 1987 OOPSLA keynote, formalized in Liskov & Wing, ["A Behavioral Notion of Subtyping"](https://dl.acm.org/doi/10.1145/197320.197383) (TOPLAS, 1994). A subtype must: not strengthen preconditions, not weaken postconditions, preserve invariants, and obey the **history constraint** (no new methods that allow state changes the supertype forbids). This is *behavioral* subtyping, stricter than what any compiler checks.
- **ISP — Interface Segregation Principle.** "Clients should not be forced to depend upon interfaces that they do not use." Derived from Martin's consulting at Xerox, where a single fat `Job` class served printing, stapling, and other clients, so a change for one client forced rebuilds for all.
- **DIP — Dependency Inversion Principle.** (a) High-level modules should not depend on low-level modules; both should depend on abstractions. (b) Abstractions should not depend on details. The key move is **ownership**: the interface belongs to the high-level module's package, so the source-code dependency points *against* the runtime call direction.

### How it relates to the broader landscape

SOLID belongs to the family of modularity principles that starts with David Parnas's information hiding ("On the Criteria To Be Used in Decomposing Systems into Modules", 1972) and continues through coupling/cohesion metrics and Meyer's Design by Contract. Siblings: **GRASP** (Larman, responsibility assignment patterns), **Martin's package principles** (REP, CCP, CRP, ADP, SDP, SAP — SOLID applied at component level), **Law of Demeter**, and the counter-proposal **CUPID** (Dan North, 2022), which replaces principles with properties (Composable, Unix philosophy, Predictable, Idiomatic, Domain-based). SOLID prescribes *structure*; CUPID describes *qualities* you want the code to have.

## Where

### Where it runs / lives in the stack

Nowhere at runtime. SOLID lives in **source-code structure**: class boundaries, interface placement, package/assembly dependency graphs, and build order. It matters most at the seam between **domain logic and infrastructure** (persistence, HTTP, messaging, clocks, file systems) and at component/module boundaries where separate teams or deployables meet.

### Where you typically encounter it

- **Clean / Hexagonal / Onion architecture** — DIP is the central rule: the domain defines ports, infrastructure implements adapters.
- **ASP.NET Core** — `Microsoft.Extensions.DependencyInjection` is built in, and the templates push constructor-injected interfaces.
- **Spring Framework** — the IoC container exists to wire DIP-shaped code; `@Service` classes receive interfaces.
- **Angular and NestJS** — hierarchical injectors built around constructor injection of abstract tokens.
- **Plugin systems** (IDE extensions, Webpack/Vite plugins, logging sinks) — OCP in its purest form: the host never changes when a new plugin ships.
- **Code review and interview rubrics** — the most common place engineers meet the vocabulary.

### Ecosystem and tooling

- **For wiring dependencies (DIP in practice):** Spring, `Microsoft.Extensions.DependencyInjection`, Autofac, Google Guice/Dagger, Python `dependency-injector`, NestJS.
- **For enforcing dependency direction:** ArchUnit (Java), NetArchTest / ArchUnitNET (.NET), `import-linter` (Python), `dependency-cruiser` (JS/TS), Nx module boundaries.
- **For spotting SRP/ISP smells:** static metrics such as LCOM (lack of cohesion of methods), afferent/efferent coupling, NDepend, SonarQube's cognitive complexity and "god class" rules.
- **For checking LSP:** contract tests — one shared test suite executed against every implementation of an interface; property-based testing (Hypothesis, jqwik, FsCheck).

## When

### When the topic emerged and why

The individual ideas predate the acronym: OCP (Meyer, 1988), LSP (Liskov, 1987/1994), and Martin's mid-1990s *C++ Report* columns on OCP, LSP, DIP and ISP (1996). Martin collected them in the 2000 paper "Design Principles and Design Patterns" and the 2002 book *Agile Software Development: Principles, Patterns, and Practices*. Michael Feathers rearranged the initials into "SOLID" around 2004.

The motivating problem was **rigid, fragile, immobile** C++ code: large systems where changing one header triggered hour-long rebuilds and unexpected breakage, and where no module could be reused without dragging its whole dependency tree along. Before SOLID, the answers were structured design (Yourdon/Constantine coupling and cohesion) and information hiding, which were phrased for procedural modules and gave less guidance on inheritance and interfaces.

### When to use it in a project

Reach for it when:
- The codebase is expected to live for years and be changed by more than one team.
- Business rules must be tested without a database, network, or wall clock.
- A dimension of variation is **already demonstrated** (two payment providers exist, a third is scheduled).
- You are publishing a library or plugin API that outside code will extend.
- Merge conflicts cluster in one file because several teams edit it for unrelated reasons (an SRP signal).

### When NOT to use it

Avoid it (or apply it lightly) when:
- The code is a script, spike, or prototype with a lifespan measured in weeks.
- There is one implementation and no concrete second one; an interface here costs a file and an indirection hop and buys nothing.
- The language offers a lighter mechanism: first-class functions, modules, or structural typing often achieve OCP/DIP without an interface-plus-class pair.
- The team cannot yet name the axes of change. Speculative abstraction locks in wrong seams, and wrong abstractions cost more to remove than duplication.
- Performance-critical inner loops where virtual dispatch or allocation per strategy object is measurable.

## How

### How it works under the hood

Every SOLID principle manipulates the **dependency graph** of the source code. The walkthrough below applies them in order to one class.

1. **Start with the violation.** `OrderService` computes totals, formats an invoice PDF, and calls `StripeClient` directly. Finance, the documents team, and payments all edit it (SRP broken); adding PayPal means a new `if` (OCP broken); tests need Stripe credentials (DIP broken).

```python
class OrderService:
    def checkout(self, order):
        total = sum(i.price * i.qty for i in order.items)   # finance rules
        pdf = render_invoice_pdf(order, total)              # documents team
        if order.method == "stripe":                        # payments team
            StripeClient(API_KEY).charge(order.card, total)
        elif order.method == "paypal":
            PayPalSdk().pay(order.account, total)
```

2. **Split by actor (SRP).** Extract `PricingPolicy`, `InvoiceRenderer`, and payment handling into separate modules. Each now has exactly one group of people who ask for changes.
3. **Introduce the abstraction where variation lives (OCP).** Define `PaymentGateway.charge(order, amount)`. Adding a provider becomes a new class; `OrderService` is no longer edited.
4. **Move ownership of the abstraction (DIP).** Put `PaymentGateway` in the domain package next to `OrderService`, not in the infrastructure package. The source-code arrow now runs `infrastructure → domain`, while the runtime call still runs `domain → infrastructure`. That reversal is the "inversion".

```
   compile-time:   [domain: OrderService, «PaymentGateway»]  ◄──  [infra: StripeGateway]
   run-time:        OrderService ──calls──► StripeGateway (via the interface)
```

5. **Keep the abstraction narrow (ISP).** If refunds are only used by the back-office tool, `Refunder` is a separate interface. `OrderService` then does not recompile or redeploy when refund signatures change.
6. **Make every implementation substitutable (LSP).** `StripeGateway` and `PayPalGateway` must accept every input the contract allows (no extra "amount must be under X" precondition), return what it promises, and raise only the errors it declares. A shared contract-test suite runs against both.
7. **Wire it at the composition root.** One place at startup (the `main` function or DI container) creates concrete classes and passes them in. That is Dependency Injection — the technique that makes DIP-shaped code runnable.

See `code/mvp.py` for a runnable version.

### Key trade-offs

| Design choice | You gain | You give up |
|---|---|---|
| Split classes by actor (SRP) | Isolated changes, fewer cross-team merge conflicts | More files; behavior spread across modules, harder to read in one sitting |
| Interface at an extension point (OCP) | New variants without editing tested code | Adding a new *operation* now means editing every implementation (the expression problem) |
| Polymorphism over `switch` (OCP) | Open for new types | Closed for new operations; a `switch`/pattern match is the opposite trade |
| Domain-owned interfaces (DIP) | Testable domain, swappable infrastructure, stable core | Indirection; "Go to definition" lands on an interface; runtime wiring errors instead of compile errors in some containers |
| Narrow role interfaces (ISP) | Smaller rebuild/redeploy blast radius, easier fakes | More types; risk of interface proliferation that mirrors every class 1:1 |
| Strict behavioral subtyping (LSP) | Callers never type-check | Some intuitive "is-a" hierarchies (Square/Rectangle, read-only list extends list) must be rejected in favor of composition |

The central tension: SRP and ISP push toward **more, smaller** pieces; readability and locality push toward **fewer, larger** ones. OCP only pays off if you guessed the axis of change correctly.

### Common failure modes

- **Interface-per-class explosion.** Every `FooService` has an `IFooService` with one implementer. Cause: DIP applied by rule rather than at real boundaries.
- **SRP as "one method per class."** Hundreds of tiny classes with no cohesion. Cause: reading "responsibility" as "task" instead of "actor".
- **Refused bequest.** A subclass overrides a method to throw `NotImplementedException`, and callers add `isinstance` checks. Cause: inheritance used for code reuse, violating LSP.
- **Wrong seam.** The abstraction varies payment provider, but the real change request varies currency handling, which cuts across every implementation. Cause: speculative OCP before the axis of change was known.
- **Leaky abstraction.** `PaymentGateway.charge()` returns a `StripeCharge` object. Cause: abstraction written from the low-level side, so the domain still depends on details (DIP part b broken).
- **Service-locator relapse.** Classes call `container.resolve<T>()` internally. Cause: DI container treated as a global, hiding dependencies and reintroducing coupling.
- **Fat interface in a shared library.** A 40-method `IRepository` forces every consumer to redeploy on any signature change. Cause: ISP ignored at package boundaries.

## Why

### Why it exists

The dominant cost of software is not writing it but changing it. Change cost scales with how far a modification propagates along dependency edges. SOLID attacks that from first principles: **cohesion** (SRP) keeps related change in one place; **stable abstractions** (OCP, ISP) stop change from crossing boundaries; **substitutability** (LSP) makes those boundaries trustworthy; **dependency direction** (DIP) ensures volatile details depend on stable policy rather than the reverse. In compiled languages this was also literal build time and deployment granularity; today it maps to test isolation, independent deployability, and team autonomy.

### Why it looks the way it does

The non-obvious choice is DIP's **inverted ownership**. The obvious alternative is a layered design where the domain calls a `StripeClient` interface published by the infrastructure layer. That still makes the domain depend on the infrastructure package: changing the infrastructure API forces the domain to change, and you cannot build the domain without it. Moving the interface into the domain means the stable, valuable code sits at the bottom of the dependency graph and the volatile code depends on it.

OCP made a similar pivot. Meyer's original OCP used implementation inheritance: extend a class, override behavior. That produced deep, fragile hierarchies (the fragile base class problem). Martin's polymorphic OCP extends via *interfaces* and composition, trading some code reuse for decoupling. The same history explains LSP's prominence: once inheritance was the extension mechanism, a formal rule was needed to say when inheritance is safe.

Finally, SOLID frames the design choice in OO terms because C++ and Java in the 1990s had no cheaper abstraction mechanism. In languages with first-class functions, a `Callable[[Order, Decimal], None]` parameter satisfies OCP and DIP without a class hierarchy. Martin himself argues in *Functional Design* (2023) that the principles still hold in functional code, just with different mechanics.

### Why it matters now

In 2026, SOLID is **stable but contested**. It remains the default vocabulary of code review, interviews, and framework documentation (Spring, ASP.NET Core, NestJS all assume DIP-shaped code). At the same time, critiques like Dan North's CUPID and the broader "avoid hasty abstractions" movement push back against the interface-everywhere style that SOLID-by-rule produces. Two current forces raise its relevance: modular monoliths, where SRP/ISP/DIP at module boundaries determine whether a later service extraction is cheap; and AI-assisted coding, where generated code tends toward either god classes or needless abstraction, making the ability to judge seams more valuable than the ability to type them.

## Open questions / things to verify in practice

- In a real codebase, how many interfaces have exactly one implementation, and would deleting them cost anything beyond test fakes?
- Can you identify the actors for your largest class from its git history (who commits, which tickets)? Does that match an SRP split?
- Does a shared contract-test suite actually catch LSP violations across your implementations, or do they diverge on error handling and edge inputs?
- When a new requirement arrives, does it land along the axis your OCP abstraction anticipated, or cut across it?
- Does an architecture test (ArchUnit, NetArchTest, `import-linter`) catch a domain-to-infrastructure import introduced in a PR?
- Where does replacing an interface with a plain function parameter reduce code without losing testability?
