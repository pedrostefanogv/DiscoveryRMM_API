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
        bool SyncPingDispatchEnabled = true,
        bool WebPushDispatchEnabled = true);

    public static BackgroundServicesConfig ReadBackgroundServicesConfig(IConfiguration configuration, bool isDevelopment)
    {
        return new BackgroundServicesConfig(
            IsDevelopment: isDevelopment,
            AlertSchedulerEnabled: configuration.GetValue<bool?>("BackgroundJobs:AlertScheduler:Enabled") ?? true,
            SyncPingDispatchEnabled: configuration.GetValue<bool?>("BackgroundJobs:SyncPingDispatch:Enabled") ?? true,
            WebPushDispatchEnabled: configuration.GetValue<bool?>("BackgroundJobs:WebPushDispatch:Enabled") ?? true);
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

        // Web push dispatch (singleton + hosted service pattern).
        // Mantem o envio fora do caminho critico de PublishAsync.
        //
        // A FILA e sempre registrada: NotificationService depende dela, entao
        // condiciona-la a flag quebraria o DI. A flag controla apenas o worker —
        // desligado, os jobs ficam na fila e sao descartados no limite.
        services.AddSingleton<IWebPushDispatchQueue, WebPushDispatchBackgroundService>();
        if (config.WebPushDispatchEnabled)
        {
            services.AddHostedService(sp => (WebPushDispatchBackgroundService)sp.GetRequiredService<IWebPushDispatchQueue>());
        }

        // Note: LogPurge, ReportRetention, AiChatRetention, P2pMaintenance,
        // KnowledgeEmbedding, AlertScheduler, SlaMonitoring, ReportGeneration,
        // and all Reconciliations have been migrated to Quartz.NET jobs.
        // See QuartzServiceCollectionExtensions.

        return services;
    }
}
