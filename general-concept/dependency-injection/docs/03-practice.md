# Dependency Injection — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a SaaS backend on ASP.NET Core, Spring Boot or NestJS, DI is the startup code (`Program.cs`, `@Configuration` classes, Nest modules) plus the per-request scope the framework opens for every HTTP call. When an engineer asks why two requests saw each other's data, or why memory climbs until a nightly restart, the answer is usually a lifetime decision in that file.

The second place is background work: queue consumers, hosted services, cron jobs. These run outside the request pipeline, so the scope the framework normally opens for you is missing. Most of the lifetime bugs listed in `02-deep-dive.md § How` show up here first.

The third place is the test suite. `WebApplicationFactory.ConfigureTestServices`, FastAPI's `app.dependency_overrides` and Spring's test context all work by replacing pieces of the production graph, so the graph's design decides whether integration tests are fast and whether they test what actually ships.

## Best practices

### 1. Use constructor injection by default and method injection for per-call data
**Do:** Required collaborators go in the constructor, stored in `readonly`/`final` fields. Request-specific values (cancellation token, current tenant, the message being handled) go in as method parameters.
**Why:** The object can't exist half-built, and code review sees every dependency. Field injection (`@Autowired` on a field, `[Inject]` properties) produces objects that throw `NullPointerException` when built with `new` in a test, and the Spring reference itself recommends constructor injection for exactly this reason.
**Avoid:** Using setter injection to make a circular dependency compile.

### 2. Keep one composition root per deployable and split it into feature modules
**Do:** Wire only at the entry point. Group registrations per feature (`services.AddBilling(config)`, a Spring `@Configuration` per module, a Nest module) so `Program.cs` reads like a table of contents.
**Why:** When wiring is spread across the codebase, "which implementation runs in prod?" takes an afternoon to answer. When it sits in one 600-line method, every feature PR conflicts with every other.
**Avoid:** Reusable libraries that need a container. A library exposes constructors and, at most, an optional `AddX()` extension.

### 3. Choose lifetimes from the object's state, not from performance guesses
**Do:** Stateless service → transient or scoped. Per-request unit of work (`DbContext`, transaction, current user) → scoped. Expensive, thread-safe, shared resource (connection pool, compiled regex cache, `SocketsHttpHandler`) → singleton.
**Why:** A singleton has to be thread-safe and keeps its whole object graph alive until shutdown. Microsoft's own guidance warns against making stateless services singletons. Most "singleton for speed" changes save nanoseconds and add a concurrency bug.
**Avoid:** A blanket `AddSingleton` for everything because "allocations are slow."

### 4. Validate the full graph before traffic arrives
**Do:** Set `ValidateScopes` and `ValidateOnBuild` to `true` in every environment (the .NET host enables them only in Development), or, if startup time is tight, add a CI test that builds the provider with validation on and resolves every controller, handler and hosted service.
**Why:** Without validation, a missing registration or a captive `DbContext` turns into a 500 on the first call to a rarely used endpoint, often days after deploy. With it, the deploy fails.
**Avoid:** Turning on Spring's `spring.main.lazy-initialization` globally for faster boot without that test. Spring's docs say directly that it delays discovery of misconfigured beans until first use.

### 5. Open an explicit scope per unit of background work
**Do:** Hosted services and consumers take `IServiceScopeFactory` (or Spring's `ObjectProvider<T>`) and create one scope per message or job: `await using var scope = scopeFactory.CreateAsyncScope();`.
**Why:** Hosted services are singletons. A `DbContext` passed into their constructor lives for days, so its change tracker grows without limit and the connection eventually breaks. In practice the scope boundary is your transaction boundary and your retry boundary too.
**Avoid:** Resolving scoped services from the root provider "just this once" in a worker.

### 6. Respect resources that rotate on their own clock
**Do:** For anything with built-in expiry (HTTP connections and DNS, rotated DB credentials, OAuth tokens), inject a factory or provider and ask it for the resource at the point of use. In .NET that means `IHttpClientFactory` (pooled handlers recycle every two minutes by default), or a singleton `HttpClient` over a `SocketsHttpHandler` with `PooledConnectionLifetime` set.
**Why:** If a singleton captures a typed `HttpClient`, it keeps the old IP after a blue/green DNS cutover. Microsoft's troubleshooting docs list "typed client captured by a singleton" as a known cause.
**Avoid:** `new HttpClient()` per call. It exhausts sockets under load.

### 7. Keep constructors and DI factories cheap, synchronous and free of I/O
**Do:** Constructors only assign fields. Warm-up work (open connections, load caches, fetch secrets) runs in an explicit startup step such as `IHostedService.StartAsync`, a Spring `ApplicationRunner` or a FastAPI lifespan handler.
**Why:** Resolution happens on the request path, and transient constructors run on every resolve. Microsoft documents that an async factory blocked with `.Result` can deadlock. A constructor that calls the network makes every test that builds the class slow or flaky.
**Avoid:** `AddSingleton(sp => LoadFromVaultAsync().Result)`.

### 8. Inject typed, validated configuration, not the raw config bag
**Do:** Bind settings into typed objects and validate them at startup: .NET `AddOptions<T>().ValidateDataAnnotations().ValidateOnStart()`, Spring `@ConfigurationProperties` with `@Validated`, pydantic `BaseSettings`.
**Why:** If you inject `IConfiguration` or `Environment` everywhere, you have a service locator for strings. A typo in `"Stripe:ApiKey"` shows up as a `null` at checkout instead of a failed boot.
**Avoid:** `config["Some:Key"]` reads inside business classes.

### 9. Treat parameter count as a design alarm, not a DI problem
**Do:** Once a constructor takes about five or more dependencies, look for a cohesive subset and extract it into a real service with a name. Seemann calls these *facade services*.
**Why:** Over-injection is a symptom of an SRP problem that DI makes visible. The class behind it is the one that collects merge conflicts and needs 15 mocks per test.
**Avoid:** Hiding the count behind property injection or a `Dependencies` record.

### 10. Make test overrides explicit, narrow and reset
**Do:** Override only the boundary you can't run in tests (payment provider, clock, LLM client) through the framework's override hook, and reset it per test (`app.dependency_overrides.clear()` in a fixture teardown). Keep the rest of the graph real.
**Why:** In Spring, the test context cache treats each distinct `@MockitoBean`/`@MockBean` combination as a new context, so scattered mocks multiply context startups and CI time. In FastAPI, an override nobody clears leaks into the next test, and test results start depending on execution order.
**Avoid:** Mocking every constructor argument, which tests the wiring of mocks rather than the code.

## Anti-patterns to recognize

- **The ambient container**: `IServiceProvider` captured in a static field, `ApplicationContextAware` stashed in a holder, or a `ServiceLocator.Current` helper. It works until two tests run in parallel or two hosts share a process, and then it resolves services from the wrong container. Pass dependencies through constructors. If code can't take them (an attribute, an EF entity), push the work to a service that can.
- **`BuildServiceProvider()` during registration**: you call it inside `ConfigureServices` to resolve one value for another registration. It builds a second container, so every "singleton" can exist twice, and ASP.NET Core flags it with analyzer ASP0000. Use the factory overload `AddSingleton<T>(sp => ...)` or the options pattern.
- **`@Lazy` / `Lazy<T>` as a cycle breaker**: you add it when startup fails with a circular reference. The cycle is still there, and the failure moves from boot to first use. Extract the shared logic into a third service that both depend on.
- **Request data in the container as a singleton**: you register "current user", tenant or shopping cart so it is injectable everywhere. Under concurrency, requests read each other's values, which is a data leak, not just a bug. Use a scoped accessor, or pass the value as a method argument.
- **Environment branches inside feature modules**: `if (env.IsProduction()) services.AddX(...)` scattered through registration code. Staging and test graphs drift from production, so the code you validated is not the code you deployed. Keep environment decisions in the composition root and have each one swap a single binding.
- **Transient `IDisposable` resolved from the root**: a factory or singleton calls `rootProvider.GetService<SomeDisposable>()` in a loop. The container keeps every instance for disposal at shutdown, so memory grows steadily until the pod is OOM-killed. Create a scope, or construct the object yourself with `ActivatorUtilities.CreateInstance` and dispose it.

## Real-world usage patterns

- **Multi-tenant B2B API.** Middleware resolves the tenant from the JWT into a scoped `TenantContext`, and repositories take it to filter every query. The non-obvious lesson: scope validation catches a singleton that *injects* `TenantContext`. It does not catch a singleton cache that *stores* a tenant-specific value it read once. Singleton caches should take the tenant as part of the cache key, passed per call.

- **Queue consumer fleet.** Workers pull from SQS or Kafka and process orders with EF Core or JPA. Each message gets its own scope, `DbContext` and transaction. Lesson: when the team moved to batch consumption for throughput, they had to decide explicitly between scope per message and scope per batch. Per-batch scope meant one poisoned message's tracked entities were flushed with the next message's commit. The scope is the failure-isolation unit.

- **Mobile app and cold-start-sensitive services.** Android apps use Dagger/Hilt, and serverless or Native AOT services move from reflection containers to generated wiring or Pure DI. Lesson: the win isn't only the milliseconds saved. A whole class of failures (missing binding, cycle, type removed by R8 or the trimmer) turns from a crash on launch into a build error.

- **LLM-backed service with an eval suite.** The model client, vector store and tool executor are injected behind small capability-level interfaces (`Completer`, `Retriever`). Evals swap in recorded-response fakes. Lesson: inject at the capability level, not the vendor SDK client. Otherwise every fake has to imitate the SDK's request and response shapes, and switching vendors rewrites the fakes along with production.

## Operational checklist

- Does the app fail to start (or CI fail) on a missing registration or a scoped service captured by a singleton, in the production configuration and not only in Development?
- Does every hosted service or consumer create its own scope per unit of work, with no scoped type in its constructor?
- Is every singleton thread-safe, and does each one have a stated reason to be a singleton?
- Are HTTP clients obtained through a factory, or built on a handler with a bounded connection lifetime, and never captured by singletons as typed clients?
- Do constructors and DI factories avoid I/O and `.Result`/`.Wait()`, with warm-up moved to an explicit startup hook?
- Is configuration bound to typed objects and validated at startup, with no raw `config["..."]` reads in business code?
- Is `IServiceProvider`/`ApplicationContext` injected only into framework-level infrastructure (factories, middleware), never into domain or application services?
- Is per-pod memory tracked over days, so root-tracked disposables show up as a slope before an OOM kill?
- Can a new engineer find, from one file, which implementation of each port runs in production and where environment-specific swaps happen?

## How this topic typically evolves in a codebase

Projects start with the framework's container and a dozen explicit registrations, and everything is readable. Around 100 services, `Program.cs` grows long enough that someone adds assembly scanning (Scrutor, Spring component scanning, Nest auto-imports). This is the first fork, and the two camps disagree. Seemann argues that convention-based registration gives the best value once conventions are strict. Teams that have been burned argue for explicit registration so that `grep` answers "what runs here?" In practice the tie-breaker is enforcement: scanning works when an architecture test pins the conventions, and turns chaotic when it doesn't.

The painful migration comes from either an incident (cross-tenant leak, memory leak, stale DNS) that forces a lifetime audit, or a platform change (Native AOT, GraalVM native image, a cold-start budget) that rules out reflection-heavy wiring. Both are expensive because lifetimes were decided one PR at a time and nobody owns the whole graph.

Mature codebases end up with feature-module composition roots, graph validation in CI, scoped units of work for all background processing, and a small set of overridable test boundaries. When a monolith is split, the composition root is the clearest map of where to cut. Modules that share no registrations can already be deployed separately.

## Further reading

- [Martin Fowler, "Inversion of Control Containers and the Dependency Injection pattern" (2004)](https://martinfowler.com/articles/injection.html): the origin of the term, and the clearest comparison of DI with Service Locator.
- [Mark Seemann & Steven van Deursen, *Dependency Injection Principles, Practices, and Patterns* (Manning, 2019)](https://www.manning.com/books/dependency-injection-principles-practices-patterns): the standard book on composition roots, lifetimes and DI anti-patterns. Most of the vocabulary above comes from it.
- [Microsoft, "Dependency injection guidelines"](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/guidelines): a list of production anti-patterns written by the runtime team (captive dependencies, async factory deadlocks, disposable leaks), and useful outside .NET too.
- [Mark Seemann, "Captive Dependency" (2014)](https://blog.ploeh.dk/2014/06/02/captive-dependency/): the lifetime bug behind most DI incidents, explained in one short post.
- [Mark Seemann, "When to use a DI Container" (2012)](https://blog.ploeh.dk/2012/11/06/WhentouseaDIContainer/): weighs Pure DI against explicit and convention-based registration. Read it before adding a container to a codebase that doesn't have one.