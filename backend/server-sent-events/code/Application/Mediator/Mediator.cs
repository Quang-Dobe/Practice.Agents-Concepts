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
            .Invoke(handler, [request, ct])!;

        return await task;
    }
}
