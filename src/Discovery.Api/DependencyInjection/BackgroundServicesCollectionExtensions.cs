using Discovery.Api.Services;
using Discovery.Api.Services.BackgroundServices;
using Discovery.Core.Interfaces;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers all background services (IHostedService) with toggles from configuration.
/// Services are grouped by environment (dev vs non-dev) and feature flags.
/// </summary>
public static class BackgroundServicesCollectionExtensions
{
    public sealed record BackgroundServicesConfig(
        bool IsDevelopment);

    public static BackgroundServicesConfig ReadBackgroundServicesConfig(IConfiguration configuration, bool isDevelopment)
    {
        return new BackgroundServicesConfig(
            IsDevelopment: isDevelopment);
    }

    public static IServiceCollection AddDiscoveryBackgroundServices(
        this IServiceCollection services,
        BackgroundServicesConfig config)
    {
        // Observability registry — shared by every IHostedService and the
        // BackgroundServicesController dashboard.
        services.AddSingleton<BackgroundServiceRegistry>();

        // Always-registered services
        services.AddScoped<AlertDispatchService>();
        services.AddHostedService<AlertSchedulerBackgroundService>();
        services.AddHostedService<AgentPackagePrebuildHostedService>();

        // Sync ping dispatch (singleton + hosted service pattern)
        services.AddSingleton<ISyncPingDispatchQueue, SyncPingDispatchBackgroundService>();
        services.AddHostedService(sp => (SyncPingDispatchBackgroundService)sp.GetRequiredService<ISyncPingDispatchQueue>());

        // Note: LogPurge, ReportRetention, AiChatRetention, P2pMaintenance,
        // KnowledgeEmbedding, AlertScheduler, SlaMonitoring, ReportGeneration,
        // and all Reconciliations have been migrated to Quartz.NET jobs.
        // See QuartzServiceCollectionExtensions.

        return services;
    }
}
