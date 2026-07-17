using Microsoft.Extensions.DependencyInjection;

namespace Application.Mediator;

// Resolves the matching IRequestHandler<TRequest,TResponse> from DI and
// invokes it via reflection. No caching, no pipelines - the point is that
// you can read the whole dispatch in ten lines.
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
            .Invoke(handler, new object[] { request, ct })!;

        return await task;
    }
}
