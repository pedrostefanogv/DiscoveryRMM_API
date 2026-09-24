using Discovery.Infrastructure.Cqrs.Behaviors;
using MediatR;

namespace Discovery.Api.Cqrs.DependencyInjection;

/// <summary>
/// Registers MediatR and CQRS pipeline behaviors.
/// </summary>
public static class CqrsServiceCollectionExtensions
{
    /// <summary>
    /// Adds CQRS infrastructure to the service collection:
    /// - MediatR with handler registration
    /// - Pipeline behaviors (logging, validation, performance, transaction)
    /// </summary>
    public static IServiceCollection AddDiscoveryCqrs(this IServiceCollection services)
    {
        // Register MediatR with all handlers from Core, Infrastructure and Api
        // assemblies. Handlers vivendo em Discovery.Core (ex. StartupItemAction,
        // SendAgentNotification) NÃO eram registrados antes, o que fazia
        // mediator.Send lançar InvalidOperationException — mapeada pelo
        // ExceptionHandlingMiddleware para 400 "Requisição inválida".
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Discovery.Core.Cqrs.VoidResult).Assembly);
            cfg.RegisterServicesFromAssembly(typeof(Discovery.Infrastructure.Cqrs.Behaviors.LoggingBehavior<,>).Assembly);
            cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
        });

        // Register pipeline behaviors in order (first registered = outermost)
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        return services;
    }
}