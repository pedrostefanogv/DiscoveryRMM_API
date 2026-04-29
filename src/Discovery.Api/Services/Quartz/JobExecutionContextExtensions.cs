using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Extension helpers used by Quartz <see cref="IJob"/> implementations to
/// resolve scoped services and create typed loggers from
/// <see cref="IJobExecutionContext"/>. Backed by an
/// <see cref="IServiceProvider"/> stored in the scheduler context under the
/// key <c>ServiceProvider</c> at startup.
/// </summary>
public static class JobExecutionContextExtensions
{
    public const string ServiceProviderKey = "ServiceProvider";

    public static T GetScopedService<T>(this IJobExecutionContext context) where T : notnull
    {
        var provider = ResolveProvider(context);
        return provider.GetRequiredService<T>();
    }

    public static ILogger<T> GetLogger<T>(this IJobExecutionContext context)
        => context.GetScopedService<ILoggerFactory>().CreateLogger<T>();

    private static IServiceProvider ResolveProvider(IJobExecutionContext context)
    {
        if (context.Scheduler.Context.TryGetValue(ServiceProviderKey, out var sp) && sp is IServiceProvider provider)
            return provider;

        throw new InvalidOperationException(
            "Root IServiceProvider was not registered in the Quartz scheduler context. " +
            "Ensure JobExecutionContextExtensions.ServiceProviderKey is set during startup.");
    }
}
