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
        bool IsDevelopment,
        bool AlertSchedulerEnabled = true,
        bool SyncPingDispatchEnabled = true);

    public static BackgroundServicesConfig ReadBackgroundServicesConfig(IConfiguration configuration, bool isDevelopment)
    {
        return new BackgroundServicesConfig(
            IsDevelopment: isDevelopment,
            AlertSchedulerEnabled: configuration.GetValue<bool?>("BackgroundJobs:AlertScheduler:Enabled") ?? true,
            SyncPingDispatchEnabled: configuration.GetValue<bool?>("BackgroundJobs:SyncPingDispatch:Enabled") ?? true);
    }

    public static IServiceCollection AddDiscoveryBackgroundServices(
        this IServiceCollection services,
        BackgroundServicesConfig config)
    {
        // Observability registry — shared by every IHostedService and the
        // BackgroundServicesController dashboard.
        services.AddSingleton<BackgroundServiceRegistry>();

        // Always-registered services
        services.AddScoped<IAlertDispatchService, AlertDispatchService>();
        services.AddHostedService<AgentPackagePrebuildHostedService>();

        // Sync ping dispatch (singleton + hosted service pattern, toggleable)
        if (config.SyncPingDispatchEnabled)
        {
            services.AddSingleton<ISyncPingDispatchQueue, SyncPingDispatchBackgroundService>();
            services.AddHostedService(sp => (SyncPingDispatchBackgroundService)sp.GetRequiredService<ISyncPingDispatchQueue>());
        }

        // Note: LogPurge, ReportRetention, AiChatRetention, P2pMaintenance,
        // KnowledgeEmbedding, AlertScheduler, SlaMonitoring, ReportGeneration,
        // and all Reconciliations have been migrated to Quartz.NET jobs.
        // See QuartzServiceCollectionExtensions.

        return services;
    }
}
