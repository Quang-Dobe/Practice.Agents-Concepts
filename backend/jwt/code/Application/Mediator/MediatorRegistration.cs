using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Mediator;

public static class MediatorRegistration
{
    // Scans the given assemblies for IRequestHandler<,> implementations
    // and registers each as scoped, plus IMediator itself.
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
