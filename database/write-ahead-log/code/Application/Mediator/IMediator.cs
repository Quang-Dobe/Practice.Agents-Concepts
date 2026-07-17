namespace Application.Mediator;

// Marker interface. The TResponse type parameter lets callers infer the
// return type at Send() and removes the need for downstream casts.
public interface IRequest<TResponse> { }

// The one method every use case implements. Handlers are one-per-command,
// resolved from DI by the Mediator below.
public interface IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken ct);
}

// The single entry-point the Console layer talks to. This is the whole
// reason we hand-roll a mediator: the Console never new()s a handler.
public interface IMediator
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default);
}
