using Microsoft.Extensions.DependencyInjection;

namespace Application.Mediator;

// Resolves the matching handler from DI and invokes it via reflection.
// No optimization here — clarity over throughput.
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
