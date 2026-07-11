using Microsoft.Extensions.DependencyInjection;

namespace Application.Mediator;

// Hand-rolled mediator: resolves the handler from DI and invokes it via reflection.
// No pipeline behaviors, no caching tricks — the topic is connection pooling,
// not CQRS plumbing. Keep the plumbing invisible.
public sealed class Mediator(IServiceProvider services) : IMediator
{
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
    {
        var handlerType = typeof(IRequestHandler<,>)
            .MakeGenericType(request.GetType(), typeof(TResponse));

        var handler = services.GetRequiredService(handlerType);

        var task = (Task<TResponse>)handlerType
            .GetMethod("Handle")!
            .Invoke(handler, new object[] { request, ct })!;

        return await task;
    }
}
