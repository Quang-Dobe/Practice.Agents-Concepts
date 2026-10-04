# Dependency Injection — Overview

> Dependency Injection (DI) means code receives the things it needs from outside instead of creating or looking them up itself. It is a technique, not a framework.

## The 30-second version

Most code needs collaborators like a database, an HTTP client, a clock or a logger. The instinctive move is to build them where you use them: `self.repo = PostgresOrderRepo()`. DI turns that around. The class declares what it needs, usually as constructor parameters, and whoever creates the class passes those in. The class stops caring *which* database or *which* clock it gets. That one change lets you test code without real infrastructure and swap implementations in one place. Martin Fowler's [2004 article](https://martinfowler.com/articles/injection.html) made the name popular, but the core idea is just "pass it in as an argument."

## The mental model

Think of a restaurant kitchen.

Chef A drives to one particular farm every morning for tomatoes and cooks on a stove they bought and installed themselves. The recipe is welded to that farm. If the farm closes, dinner is cancelled. If you want to try the recipe at home, you have to drive to that farm too.

Chef B posts a list at their station: "tomatoes, flour, a stove." Before service, the kitchen manager stocks the station. Chef B cooks with whatever arrives, as long as it is a tomato. If the manager switches suppliers, Chef B never notices. On training day, the manager stocks cheap practice tomatoes.

Chef B is DI. The list at the station is the constructor signature. The kitchen manager is the **composition root**: the one place, at app startup, where the real objects are built and wired together.

```python
# Chef A: builds its own dependency
class OrderService:
    def __init__(self):
        self.repo = PostgresOrderRepo(DB_URL)

# Chef B: receives it
class OrderService:
    def __init__(self, repo: OrderRepo):
        self.repo = repo

OrderService(PostgresOrderRepo(DB_URL))   # production
OrderService(InMemoryOrderRepo())         # unit test
```

The manager can be a person doing the wiring by hand (plain code in `main`) or a staffing agency working from a chart (a DI container). Both count as DI. The container only automates the wiring.

## What it is NOT

- Not a DI container. Spring, .NET's `IServiceCollection` and Angular's injector automate the wiring. Passing arguments by hand is already DI.
- Not the Dependency Inversion Principle. DIP says high-level code should depend on abstractions. DI is one way to deliver the concrete object. They work well together, but they are separate ideas.
- Not the whole of Inversion of Control. IoC is the broad idea of "the framework calls you." DI is one specific case of it, where control over *creating dependencies* moves outside the class.
- Not a Service Locator. A locator lets a class reach into a global registry (`Locator.get(Repo)`), so its needs stay hidden inside the method body. With DI, every need is visible in the signature.
- Not "interfaces everywhere." You can inject a concrete class or a plain function.

## When you would reach for it

- You want unit tests that run without a real database, network or system clock.
- The right implementation depends on the environment, such as local disk in dev and S3 in production.
- Several objects should share one expensive instance, such as a connection pool or an HTTP client.
- You want to see everything a class needs just by reading its constructor.
- The app has grown large enough that hand-wiring in `main` is a chore. That is the point where a container starts to pay off.

## When you would NOT reach for it

- Throwaway scripts. A 50-line script does not need an injectable clock.
- Value objects and pure helpers like `Money` or `slugify()`. There is nothing to swap.
- Adding a one-implementation interface to every class "so it can be injected." That is ceremony, not design.
- When container auto-wiring has become so magical that nobody can say which implementation runs in production. You give up the ability to read the wiring and get surprises at startup instead.

## Key vocabulary (just enough to keep reading)

- **Dependency**: an object or service a piece of code needs to do its job.
- **Client**: the code that uses a dependency (the chef).
- **Injector**: whatever builds dependencies and hands them to clients (the kitchen manager).
- **Constructor injection**: passing dependencies through the constructor. This is the default, because the object cannot exist without them.
- **Setter injection**: assigning dependencies after construction. This is usually for optional ones.
- **Composition root**: the single place near the entry point where the object graph is assembled.
- **DI container**: a library that builds the object graph from registrations.
- **Lifetime**: how long an injected instance lives. The names vary by framework. .NET uses *singleton* (one per app), *scoped* (one per request) and *transient* (new every time).

## What's next

The next document answers What / Where / When / How / Why in detail. It covers the three injection styles side by side, building a composition root by hand, what a container does under the hood, lifetime bugs like a singleton holding on to a per-request object, and why Service Locator is widely treated as an anti-pattern.
