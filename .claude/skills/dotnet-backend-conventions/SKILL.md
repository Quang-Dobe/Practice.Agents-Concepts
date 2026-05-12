---
name: dotnet-backend-conventions
description: Use this skill whenever writing or reviewing C# / .NET code for a backend learning demo. Defines coding conventions, the Clean Architecture layering, and a hand-rolled custom Mediator pattern (no MediatR or other third-party CQRS libraries) that every .NET MVP in this repo should follow. Load before the `code-implementer` agent starts a backend topic, or when reviewing existing .NET code.
---

# .NET Backend Conventions

These conventions apply to every demo generated for a topic in the `backend/` category (and `database/` and `cloud/` when the demo is app-side C# code).

The goal is **a tiny but architecturally honest** demo: small enough to read in 5 minutes, structured enough that the reader sees how real .NET teams organize code.

## Runtime & tooling

- **.NET 8.0 or newer.** Use the latest LTS by default.
- **C# 12+ language features** are fair game: primary constructors, collection expressions, file-scoped namespaces, `required` members, raw string literals.
- **Nullable reference types: enabled.** `<Nullable>enable</Nullable>` in every `.csproj`.
- **Implicit usings: enabled.** `<ImplicitUsings>enable</ImplicitUsings>`.
- **No third-party CQRS / Mediator libraries.** Never use MediatR, MassTransit, Brighter, Wolverine, or similar. We hand-roll a tiny mediator (defined below). This rule exists because the *point* of these demos is to understand the pattern, not to learn a library.
- **Allowed third-party packages**, used sparingly:
  - `Microsoft.Extensions.DependencyInjection` — always (it's how registration works in .NET).
  - `Microsoft.Extensions.Hosting` — for console hosts.
  - `Dapper` — allowed for SQL-touching demos; it's a thin micro-ORM, not a framework.
  - `Npgsql`, `StackExchange.Redis`, `Confluent.Kafka`, etc. — allowed when the topic requires them.
  - **Forbidden**: MediatR, AutoMapper, FluentValidation, Entity Framework Core (for these demos — too much ceremony for an MVP), Polly (unless the topic *is* resilience).
  - In short: a package is allowed only if removing it would require reimplementing the topic itself.

## Clean Architecture layering

Every backend MVP uses the same four-project layout. Yes, even when the topic is small. The shape is the lesson.

```
code/
├── Domain/
│   └── Domain.csproj
│       └── (entities, value objects, domain events — zero external dependencies)
├── Application/
│   └── Application.csproj             → references: Domain
│       └── (use cases as Commands/Queries, the Mediator abstraction, port interfaces)
├── Infrastructure/
│   └── Infrastructure.csproj          → references: Application, Domain
│       └── (adapters: DB, message bus, external HTTP clients — implementations of the ports)
├── Api/                               → or Console/ for non-HTTP demos
│   └── Api.csproj                     → references: Application, Infrastructure
│       └── (composition root: DI wiring, endpoints, Program.cs)
├── Mvp.sln
└── README.md
```

**The dependency rule:** dependencies point inward. `Domain` depends on nothing. `Application` depends only on `Domain`. `Infrastructure` and `Api` depend on `Application` (and may depend on `Domain` directly).

**The omission rule:** if a layer would be empty for a given topic, **keep the project anyway with a single placeholder file**. The reader is learning the shape. Hiding empty layers breaks the muscle memory we're trying to build.

### What goes where — quick reference

| Layer | Contains | Does NOT contain |
|---|---|---|
| `Domain` | Entities, value objects, domain events, domain exceptions, enums. Pure C#. | DB attributes, JSON attributes, framework references, `async` (domain is usually synchronous). |
| `Application` | `ICommand<TResponse>` / `IQuery<TResponse>` records, their handlers, port interfaces (`IUserRepository`, `IEmailSender`), the `IMediator` abstraction, pipeline behaviors. | Concrete implementations of ports, HTTP, SQL strings. |
| `Infrastructure` | Repository implementations, DB connections, HTTP clients, message bus clients, file system access. | Business rules, anything an Application handler should own. |
| `Api` / `Console` | `Program.cs`, DI registration, minimal API endpoints / console main, JSON DTOs / request models. | Business logic. Endpoints are 3–5 lines that call the mediator. |

## The custom Mediator

This is the load-bearing part. Every backend demo wires through this mediator instead of calling handlers directly. It lives in `Application/Mediator/`.

### The abstraction (`Application/Mediator/IMediator.cs`)

```csharp
namespace Application.Mediator;

// Marker interfaces. The TResponse type parameter lets us infer return types
// at the call site and removes the need for casts.
public interface IRequest<TResponse> { }

public interface IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken ct);
}

public interface IMediator
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default);
}
```

### The implementation (`Application/Mediator/Mediator.cs`)

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Application.Mediator;

// Resolves the matching handler from DI and invokes it.
// No reflection caching tricks — this is a learning demo, not a benchmark.
public sealed class Mediator(IServiceProvider services) : IMediator
{
    public async Task<TResponse> Send<TResponse>(
        IRequest<TResponse> request,
        CancellationToken ct = default)
    {
        var handlerType = typeof(IRequestHandler<,>)
            .MakeGenericType(request.GetType(), typeof(TResponse));

        // GetRequiredService throws a clear error if the handler isn't registered.
        var handler = services.GetRequiredService(handlerType);

        // Dynamic dispatch on Handle. The runtime cost is negligible for our scale.
        var task = (Task<TResponse>)handlerType
            .GetMethod("Handle")!
            .Invoke(handler, new object[] { request, ct })!;

        return await task;
    }
}
```

### Registration helper (`Application/Mediator/MediatorRegistration.cs`)

```csharp
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Mediator;

public static class MediatorRegistration
{
    // Scans the calling assembly for IRequestHandler<,> implementations
    // and registers each as scoped, plus registers IMediator itself.
    public static IServiceCollection AddCustomMediator(
        this IServiceCollection services,
        params Assembly[] handlerAssemblies)
    {
        services.AddScoped<IMediator, Mediator>();

        foreach (var asm in handlerAssemblies)
        {
            var handlers = asm.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .SelectMany(t => t.GetInterfaces()
                    .Where(i => i.IsGenericType &&
                                i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
                    .Select(i => (Service: i, Implementation: t)));

            foreach (var (service, impl) in handlers)
                services.AddScoped(service, impl);
        }

        return services;
    }
}
```

That's it. Three files, ~60 lines total. **Do not extend this with pipeline behaviors, notifications, streams, or polymorphic dispatch unless the topic specifically requires it.** Most demos will not.

### When the topic *is* about pipeline behaviors

If the user is learning, for example, "CQRS validation pipeline", *then* add an `IPipelineBehavior<TRequest, TResponse>` to the Application/Mediator folder and wire it through the `Send` method. Otherwise, leave it out — the simpler the mediator, the clearer the demo.

## Naming & code style

- **File-scoped namespaces.** `namespace Application.Users;` then code, no braces.
- **PascalCase** for everything public: types, members, properties. **camelCase** for parameters and locals. **`_camelCase`** for private fields (rare — prefer primary constructors).
- **One public type per file.** File name matches type name.
- **`record`** for DTOs, commands, queries, and value objects. **`class`** (often `sealed`) for entities and services. **`struct`** only when the topic demands it.
- **`sealed` by default** for non-domain classes. Inheritance is opt-in.
- **`async`/`await` all the way down.** Never `.Result` or `.Wait()`. Pass `CancellationToken` through.
- **Don't suffix async methods with `Async`** in this codebase. We prefer the cleaner Microsoft-recent style; cancellation tokens make the asyncness obvious.
- **Use primary constructors** (C# 12) for DI: `public sealed class GetUserHandler(IUserRepository repo) : IRequestHandler<...>` — no boilerplate field assignments.
- **Use collection expressions** (`[1, 2, 3]`) over `new[]` / `new List<int>()`.
- **`required`** for non-nullable properties on records/classes that don't have a constructor setting them.

## Commands and queries — the shape

Every use case in `Application/` is one of:

```csharp
// A Command — mutates state, returns a result (or Unit-ish wrapper).
public sealed record CreateUserCommand(string Email, string Name)
    : IRequest<Guid>;

public sealed class CreateUserHandler(IUserRepository repo)
    : IRequestHandler<CreateUserCommand, Guid>
{
    public async Task<Guid> Handle(CreateUserCommand cmd, CancellationToken ct)
    {
        var user = User.Create(cmd.Email, cmd.Name);
        await repo.Add(user, ct);
        return user.Id;
    }
}
```

```csharp
// A Query — read-only, returns data.
public sealed record GetUserByIdQuery(Guid Id) : IRequest<UserDto?>;

public sealed class GetUserByIdHandler(IUserRepository repo)
    : IRequestHandler<GetUserByIdQuery, UserDto?>
{
    public async Task<UserDto?> Handle(GetUserByIdQuery query, CancellationToken ct)
    {
        var user = await repo.GetById(query.Id, ct);
        return user is null ? null : new UserDto(user.Id, user.Email, user.Name);
    }
}
```

**Endpoints stay anorexic.** A minimal API endpoint should be three lines:

```csharp
app.MapPost("/users", async (CreateUserCommand cmd, IMediator mediator, CancellationToken ct) =>
    Results.Ok(await mediator.Send(cmd, ct)));
```

If the endpoint grows beyond that, the work belongs in a handler, not in the route.

## Anti-patterns to avoid

- **Bringing in MediatR** — the whole point is the hand-rolled mediator. The user is learning the pattern, not the library.
- **Anemic domain models** — if `Domain` is nothing but property bags, the demo isn't using Clean Architecture, it's just over-foldered. Put real behavior on entities.
- **Repositories that leak `IQueryable`** — repositories return materialized data (`Task<User?>`, `Task<List<User>>`), not query trees. Otherwise the abstraction is fake.
- **Static helpers in `Domain`** that talk to `DateTime.UtcNow` or `Guid.NewGuid()` directly — inject `TimeProvider` (built into .NET 8) and a `Func<Guid>` factory if the topic is about determinism. For most demos, calling these directly inside `User.Create` is fine.
- **`Task.Run`** to make sync code async. If the work is sync, expose it as sync.
- **Generic repositories** (`IRepository<T>`) for a demo — they encourage anemic design and hide what the demo is actually doing.

## File layout — full example

For a "JWT authentication" backend topic:

```
code/
├── Mvp.sln
├── README.md
├── Domain/
│   ├── Domain.csproj
│   ├── Users/
│   │   ├── User.cs
│   │   └── HashedPassword.cs
│   └── Tokens/
│       └── AccessToken.cs
├── Application/
│   ├── Application.csproj
│   ├── Mediator/
│   │   ├── IMediator.cs
│   │   ├── Mediator.cs
│   │   └── MediatorRegistration.cs
│   ├── Abstractions/
│   │   ├── IUserRepository.cs
│   │   └── ITokenIssuer.cs
│   └── Auth/
│       ├── LoginCommand.cs        // record + handler in one file is OK
│       └── LoginCommandHandler.cs // or split, pick one and be consistent
├── Infrastructure/
│   ├── Infrastructure.csproj
│   ├── Persistence/
│   │   └── InMemoryUserRepository.cs
│   └── Tokens/
│       └── HmacTokenIssuer.cs
└── Api/
    ├── Api.csproj
    └── Program.cs
```

Small topic → smaller tree, but the **four projects always exist**. That's the lesson the layout teaches.

## What a finished .NET MVP should feel like

A reader who knows C# but doesn't know the topic should be able to:
1. Open the solution in their editor.
2. Find the use case in `Application/<feature>/` and read both the command/query record and its handler in under a minute.
3. Follow the dependency chain: endpoint → mediator → handler → port → adapter. Each hop is one click.
4. Run `dotnet run --project Api` and see the topic work.
5. Recognize that the mediator they're looking at is the *same one* they'll see in the next topic — the architecture transfers.

If any of those five are not true, the demo needs another pass.
