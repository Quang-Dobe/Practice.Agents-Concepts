using Microsoft.Extensions.DependencyInjection;

namespace Application.Mediator;

// Hand-rolled mediator — resolves the matching handler from DI and invokes it.
// No reflection caching, no pipeline behaviors: this is the smallest viable form.
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
