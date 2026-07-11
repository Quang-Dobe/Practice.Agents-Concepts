using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Mediator;

public static class MediatorRegistration
{
    // Scans the passed assemblies for IRequestHandler<,> implementations and registers
    // each one, plus registers IMediator itself. Scoped is the default in real apps —
    // this demo uses fresh scopes per Send.
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
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
                    .Select(i => (Service: i, Implementation: t)));

            foreach (var (service, impl) in handlers)
                services.AddScoped(service, impl);
        }

        return services;
    }
}
