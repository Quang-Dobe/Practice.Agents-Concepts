# Dependency Injection — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Dependency Injection (DI) is a technique where a component's collaborators are supplied by an external party, the **injector**, instead of being constructed or looked up by the component itself. The component declares its dependencies as part of its public contract (constructor parameters, settable properties, or method parameters). Building the object graph moves to one place outside the component, the **composition root**.

DI is a specific case of Inversion of Control (IoC). The control being inverted is the *acquisition* of dependencies. DI makes the Dependency Inversion Principle easy to apply, because the injector can hand over any implementation of an abstraction. It does not require that principle, though: injecting a concrete class or a plain function is still DI.

### The core building blocks

- **Service (dependency)**: any object or function a component needs to do its work, such as a repository, clock, HTTP client or logger.
- **Client (consumer)**: the component that receives the service. Its constructor signature is the list of what it needs.
- **Injection point**: where the service enters the client. [Fowler (2004)](https://martinfowler.com/articles/injection.html) named three: *constructor* (PicoContainer), *setter* (Spring) and *interface* injection (Avalon). Interface injection is mostly historical. *Method injection* (the dependency is passed per call) replaced it in practice.
- **Composition root**: the single place near the entry point (`main`, `Program.cs`, the application factory) where concrete types are chosen and the graph is assembled.
- **Injector**: whatever runs the composition. Either hand-written code, which Mark Seemann calls [Pure DI](https://blog.ploeh.dk/2014/06/10/pure-di/) (formerly "Poor Man's DI"), or a **DI container** that builds the graph from registrations.
- **Registration**: a container entry that maps a *service key* (usually a type, sometimes type plus name/key) to a *recipe* (implementation type, factory delegate, or pre-built instance) and a **lifetime**.
- **Lifetime / scope**: the rule for when the injector reuses an instance and when it creates a new one, and who disposes it. Common set: singleton, scoped (per request or unit of work), transient.

### How it relates to the broader landscape

DI belongs to the family of *dependency acquisition* strategies. Its siblings answer the same question ("how does this code get its collaborators?") differently:

| Strategy | How the dependency arrives | Visible in the signature? |
|---|---|---|
| Direct construction (`new`) | Client builds it | No |
| Singleton / global / ambient context | Client reads a static | No |
| Service Locator | Client asks a registry at runtime | No |
| Factory / Abstract Factory | Client asks an injected factory | Yes (the factory is injected) |
| **DI** | Injector passes it in | Yes |
| Module patching (`unittest.mock.patch`, `jest.mock`) | Test rewrites the import binding | No; works only in dynamic module systems |

In functional programming the same idea appears as partial application, the Reader monad, or Scala's `given`/`using` context parameters.

## Where

### Where it runs / lives in the stack

DI is not a layer. It is an application-composition concern that lives in two places:

1. **Constructor signatures across the codebase.** This is where each class states its needs.
2. **The composition root at process startup.** This is where the graph is built.

A container is an in-process library with no network or storage component. Web frameworks wire the container into the request pipeline: they create a child **scope** per HTTP request and resolve controllers or handlers from it. DI therefore touches the application layer and the framework boundary, not transport or persistence.

### Where you typically encounter it

- **ASP.NET Core / .NET Generic Host**: `Microsoft.Extensions.DependencyInjection` is built in. Controllers, middleware, hosted services and Razor components are all resolved through it. The ASP.NET Core templates register more than 250 framework services by default ([docs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection)).
- **Spring Framework / Spring Boot**: the `ApplicationContext` is a DI container, and IoC was Spring's founding feature. Since Spring 4.3, a class with a single constructor gets constructor injection with no `@Autowired` annotation ([Spring blog](https://spring.io/blog/2016/03/04/core-container-refinements-in-spring-framework-4-3)).
- **Angular**: hierarchical injectors. An `ElementInjector` tree follows the DOM, and an `EnvironmentInjector` tree handles application-wide providers. The `inject()` function (Angular 14+) supplements constructor injection ([angular.dev](https://angular.dev/guide/di/hierarchical-dependency-injection)).
- **Android**: Dagger 2 and Hilt generate the wiring code at compile time with no runtime reflection ([Dagger README](https://android.googlesource.com/platform/external/dagger2/+/refs/heads/main)).
- **FastAPI**: `Depends()` injects per-request dependencies into path-operation functions, a form of method injection.
- **pytest**: fixtures are DI keyed by parameter name. The test function declares `db`, and pytest builds and tears down the `db` fixture.

### Ecosystem and tooling

- **Runtime (reflection-based) containers**: `Microsoft.Extensions.DependencyInjection`, Autofac (.NET); Spring IoC, Google Guice (JVM); NestJS, InversifyJS, tsyringe (TypeScript); `dependency-injector` (Python); Uber `dig`/`fx` (Go).
- **Compile-time / code-generation injectors**: Dagger 2 and Hilt (JVM/Android); Google Wire (Go; the repository was [archived in August 2025](https://github.com/google/wire) as feature-complete); source-generator containers in .NET; Spring Boot 3's AOT processing, which precomputes bean definitions for GraalVM native images.
- **Standards**: [JSR-330](https://jcp.org/en/jsr/detail?id=330) (`javax.inject`, final release October 2009, co-led by Rod Johnson of SpringSource and Bob Lee of Google), now `jakarta.inject`; Jakarta CDI builds scopes and lifecycle on top of it.
- **Graph validation**: .NET `ServiceProviderOptions.ValidateScopes` / `ValidateOnBuild`; Spring Boot's startup failure on circular references (default since 2.6); Dagger's compile errors.

## When

### When the topic emerged and why

In the late 1990s, enterprise Java code either called `new` on concrete collaborators, used GoF Singletons, or looked them up through JNDI (EJB 2's service-locator style). All three tied business logic to infrastructure, so testing it meant running inside an application server. Lightweight containers appeared in response: Apache Avalon (interface injection), then PicoContainer (constructor injection) and Spring (setter injection) around 2003. In January 2004, Fowler's article coined the name "Dependency Injection" to set the pattern apart from both "IoC" (too generic) and Service Locator. Later milestones: Guice (2007), JSR-330 (2009), Seemann's ["Service Locator is an Anti-Pattern"](https://blog.ploeh.dk/2010/02/03/ServiceLocatorisanAnti-Pattern/) (2010), Dagger (2012, then Google's Dagger 2 in 2015), and ASP.NET Core shipping a container in the box (2016). DI went from an opt-in library to a default platform feature.

### When to use it in a project

Reach for it when:

- Code touches non-deterministic or slow resources (DB, network, clock, randomness, filesystem) and you want fast, isolated unit tests.
- The implementation varies by environment, tenant, or feature flag, and you want that choice made in one place.
- Several components should share one instance with a managed lifetime, such as a connection pool, `HttpClient`, or cache.
- Per-request state (unit of work, current user, DB transaction) has to flow to many components without being threaded through every method.
- The framework you already use is built around a container (ASP.NET Core, Spring, Angular, NestJS). Fighting it costs more than using it.

### When NOT to use it

Avoid it when:

- The code is a short script or CLI where `main` builds two objects. Use plain functions with arguments.
- The "dependency" is a value object or pure function (`Money`, `slugify`). There is nothing to substitute and no lifetime to manage.
- You would add a single-implementation interface to every class only so it can be mocked. Inject the concrete class, or test against a real in-memory implementation.
- You are adding a container to a small or medium app with no framework container. Pure DI gives you compile-time checking and readable wiring for free.
- The language idiom resists it. Idiomatic Go wires by hand in `main`, and Python tests often use `monkeypatch` for one-off seams.

## How

### How it works under the hood

**Pure DI** is ordinary code. The composition root calls constructors in dependency order and holds shared instances in local variables:

```python
def compose(cfg) -> App:
    pool    = ConnectionPool(cfg.db_url)          # "singleton": one local, reused
    clock   = SystemClock()
    def handle_request(req):                       # "scoped": built per request
        uow   = UnitOfWork(pool)
        repo  = OrderRepo(uow)
        svc   = OrderService(repo, clock)
        return OrderController(svc).handle(req)
    return App(handle_request)
```

A **container** automates exactly this. A typical reflection-based container (the description matches `Microsoft.Extensions.DependencyInjection`, Spring and Guice in broad strokes) goes through this lifecycle:

1. **Register.** The app adds descriptors `(service key → recipe, lifetime)` to a mutable collection. In .NET, registering the same key twice means the last registration wins for a single resolve, while `IEnumerable<T>` returns all of them in order. Keyed registrations (.NET 8+ `AddKeyed*`, Spring `@Qualifier`, Guice binding annotations) disambiguate several implementations of one type.
2. **Build.** The collection is frozen into a lookup table and a root provider is created. Optionally the full graph is validated now. In .NET's default host, `ValidateScopes` and `ValidateOnBuild` are both set to `IsDevelopment()` ([source](https://source.dot.net/Microsoft.Extensions.Hosting/HostingHostBuilderExtensions.cs.html)), so production builds skip these checks unless you opt in.
3. **Resolve `T`.** The container finds the descriptor and checks the lifetime cache. On a miss it **selects a constructor**. .NET picks the constructor with the most parameters it can resolve and throws if two candidates are equally good ([docs](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview)). It then resolves each parameter recursively, depth-first. A "currently resolving" stack detects cycles and fails with a circular-dependency error.
4. **Construct and cache.** It invokes the constructor. Singletons are cached in the root, scoped instances in the current scope, transients nowhere. Every `IDisposable` the container creates is recorded by the scope that created it.
5. **Compile the plan.** Reflection is expensive, so containers cache a per-service resolution plan (a "call site" in .NET) and may compile it to a delegate. After warm-up, a resolve costs close to a few dictionary lookups plus the constructor calls.
6. **End the scope.** When the request ends, the framework disposes the scope, and the scope disposes its tracked instances in reverse creation order. Singletons are disposed only when the root provider is disposed at shutdown.

```
Root provider ── singletons (live until process shutdown)
 ├─ Scope: request A ── scoped instances, disposed at end of A
 │                     └─ transients: new on every resolve, disposed with A if IDisposable
 └─ Scope: request B ── its own scoped instances
```

The governing invariant: **a component may depend only on components whose lifetime is equal to or longer than its own.** A singleton may take a singleton. A scoped component may take a scoped component or a singleton. When the rule is broken, the longer-lived object traps the shorter-lived one. Seemann calls this a *captive dependency*.

**Compile-time injectors** (Dagger, Wire) run steps 1–3 inside the build. They emit generated source equivalent to the Pure DI `compose()` above. Missing bindings and cycles become compile errors, and nothing is reflected at runtime.

### Key trade-offs

| Design choice | You gain | You give up |
|---|---|---|
| Constructor injection (default) | Object is valid once constructed; fields can be `final`/`readonly`; needs are visible | Long parameter lists show up immediately (which is also a useful signal) |
| Setter / field injection | Optional dependencies; can break cycles | Object can exist half-initialised; hidden needs; no immutability |
| Method injection | Per-call variation (request context, middleware `InvokeAsync`) | Every caller must supply the dependency |
| Pure DI | Compiler checks the graph; wiring is readable; zero startup cost | Hand-maintained wiring; lifetimes managed by hand |
| Runtime container | Auto-wiring, scopes, disposal, decorators, framework integration | Errors at startup or first request instead of at compile time; reflection cost; conflicts with AOT and trimming |
| Compile-time container | Compile-time errors; no reflection; fast cold start | Build step, generated code, slower builds, less dynamic registration |
| Assembly scanning / auto-registration | Less boilerplate | Nobody can point to the line that chose the implementation |
| DI vs Service Locator | Explicit contracts; no dependency on the locator API | Locator is quicker to retrofit into legacy code |
| DI vs module patching | Works in any language and makes seams explicit | Patching needs no production-code change in Python/JS |

### Common failure modes

- **Captive dependency.** A singleton holds an EF Core `DbContext` or session, and requests start seeing each other's data or fail on concurrent use. Cause: the lifetime invariant is broken, and scope validation is off outside Development.
- **Scoped service resolved from the root.** Memory grows with no apparent bound. Cause: the "scoped" instance lives in the root provider and is effectively promoted to singleton.
- **Transient `IDisposable` resolved from root or a long-lived scope.** A leak until shutdown. Cause: the container tracks every disposable it creates for disposal and so holds a reference to each one.
- **Background worker takes a scoped service in its constructor.** A stale or broken connection hours later. Cause: hosted services are singletons. Inject `IServiceScopeFactory` and create a scope per unit of work.
- **Missing registration.** Production returns a 500 on the first call to a rarely used endpoint. Cause: graph validation runs only in Development or not at all.
- **Circular dependency.** Startup fails (`BeanCurrentlyInCreationException`, .NET "circular dependency was detected"). Cause: two services need each other, which usually means a responsibility is split in the wrong place.
- **Silent override.** A test or library registration replaces the production implementation. Cause: last-registration-wins semantics.
- **Constructor over-injection.** A class takes 8 or more dependencies. Cause: an SRP violation that DI makes visible but does not cause.
- **Service locator in disguise.** `IServiceProvider` or `ApplicationContext` gets injected and queried inside methods. Cause: dependencies are hidden again, and failures move from startup to runtime.
- **Reflection broken by trimming or obfuscation.** A release build crashes on resolve (R8/ProGuard on Android, .NET trimming/Native AOT). Cause: types reached only through reflection were removed by the build tool.

## Why

### Why it exists

DI separates **using** an object from **choosing and building** it. Choosing an implementation is an application-wide policy: which database, which environment, how long an instance lives. Using it is local mechanism. When a class mixes the two, every policy change edits business code, every test pulls in real infrastructure, and lifetime decisions spread across hundreds of `new` calls. Moving construction to one composition root gives you:

- **Substitutability**: tests and environments swap implementations without touching consumers.
- **Explicit contracts**: the constructor lists every need, so coupling shows up in code review.
- **Centralised lifetime management**: pools, caches and transactions are created, shared and disposed by one owner.

### Why it looks the way it does

**Why injection rather than a locator?** Fowler in 2004 treated the two as close alternatives and gave Service Locator "a slight edge" for application code because it is more straightforward. The industry went the other way. A locator call compiles whether or not the service is registered, so missing dependencies surface at runtime, inside a method, on whichever code path runs first. Every class also takes a dependency on the locator API. Constructor injection moves both problems to construction time, where a container (or the compiler, with Pure DI) can check the whole graph at once. Microsoft's ASP.NET Core guidance now says outright to avoid the service locator pattern, including injected factories that resolve services at runtime.

**Why type-keyed containers?** The type system already describes the contract. Keying registrations by type lets the container read a constructor signature and wire it automatically, with no configuration per class. The cost is ambiguity when one type has several implementations, which is why keyed services and qualifiers exist.

**Why do containers own disposal?** The client did not create the dependency and may share it with others, so it cannot safely dispose it. The rule "whoever creates it disposes it" puts disposal on the injector, and that rule is what makes scopes necessary.

**Why the move toward compile-time wiring?** Reflection-based resolution costs startup time and conflicts with ahead-of-time compilation and tree-shaking. Dagger made this trade on Android, where cold start and R8 matter. Spring Boot 3 AOT and .NET Native AOT apply the same pressure on the server.

### Why it matters now

In 2026, DI is **stable and built in** on the JVM and .NET, and it is the default in Angular and NestJS. You rarely choose whether to use it. You choose how much of the container's automation to accept. The live changes are at the edges:

- **Compile-time and AOT**: serverless cold starts and native images favour generated wiring over reflection.
- **TypeScript**: the standard decorators in TS 5.0 support neither parameter decorators nor `emitDecoratorMetadata`. Reflection-based TS containers (NestJS, InversifyJS) therefore still depend on the legacy `experimentalDecorators` mode, and Angular has moved toward the `inject()` function.
- **Go**: Wire's archival reinforces the idiom of hand-wiring in `main`.
- **LLM-backed systems**: model clients, vector stores and tool executors are exactly the slow, non-deterministic dependencies DI was built to isolate. Injecting them is what makes deterministic fakes possible in evals and tests.

## Open questions / things to verify in practice

- Does my production build validate the container graph at startup? In .NET, `ValidateOnBuild` is off outside Development by default. What does a missing registration actually look like under load?
- When the same service is registered twice (by me, a library, and a test override), which one wins, and do I get any warning?
- Can I write a test that fails if any singleton transitively depends on a scoped service?
- What do container build and first-resolve cost in cold-start time for my app, and how does that compare with a Pure DI composition root?
- Does my container survive trimming, Native AOT, GraalVM native image, or R8 without manual keep-rules?
- At what constructor-parameter count does my team agree a class needs splitting, and can a linter enforce it?
