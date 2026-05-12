using Microsoft.Extensions.DependencyInjection;

namespace Application.Mediator;

// Hand-rolled mediator. Resolves the matching handler from DI and invokes
// it via reflection. No pipeline behaviors, no caching tricks — the point
// is to see the pattern, not benchmark it.
public sealed class Mediator(IServiceProvider services) : IMediator
{
    public async Task<TResponse> Send<TResponse>(
        IRequest<TResponse> request,
        CancellationToken ct = default)
    {
        var handlerType = typeof(IRequestHandler<,>)
            .MakeGenericType(request.GetType(), typeof(TResponse));

        var handler = services.GetRequiredService(handlerType);

        var task = (Task<TResponse>)handlerType
            .GetMethod("Handle")!
            .Invoke(handler, [request, ct])!;

        return await task;
    }
}
