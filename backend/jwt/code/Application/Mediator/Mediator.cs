using Microsoft.Extensions.DependencyInjection;

namespace Application.Mediator;

// Resolves the IRequestHandler<TReq, TResp> matching the runtime request type
// and invokes Handle via reflection. No third-party mediator library — the point
// of the demo is to see the pattern, not to depend on MediatR.
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
