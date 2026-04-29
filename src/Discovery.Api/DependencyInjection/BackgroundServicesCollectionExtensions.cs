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
        bool KnowledgeEmbeddingEnabled,
        bool KnowledgeEmbeddingQueueEnabled,
        bool SlaMonitoringEnabled,
        bool ReportGenerationEnabled,
        bool AgentLabelingReconciliationEnabled,
        bool MeshCentralIdentityReconciliationEnabled,
        bool MeshCentralGroupPolicyReconciliationEnabled);

    public static BackgroundServicesConfig ReadBackgroundServicesConfig(IConfiguration configuration, bool isDevelopment)
    {
        return new BackgroundServicesConfig(
            IsDevelopment: isDevelopment,
            KnowledgeEmbeddingEnabled: configuration.GetValue<bool?>("BackgroundJobs:KnowledgeEmbeddingEnabled") ?? true,
            KnowledgeEmbeddingQueueEnabled: configuration.GetValue<bool?>("BackgroundJobs:KnowledgeEmbeddingQueueEnabled") ?? false,
            SlaMonitoringEnabled: configuration.GetValue<bool?>("BackgroundJobs:SlaMonitoringEnabled") ?? true,
            ReportGenerationEnabled: configuration.GetValue<bool?>("BackgroundJobs:ReportGenerationEnabled") ?? true,
            AgentLabelingReconciliationEnabled: configuration.GetValue<bool?>("BackgroundJobs:AgentLabelingReconciliationEnabled") ?? true,
            MeshCentralIdentityReconciliationEnabled: configuration.GetValue<bool?>("BackgroundJobs:MeshCentralIdentityReconciliationEnabled") ?? true,
            MeshCentralGroupPolicyReconciliationEnabled: configuration.GetValue<bool?>("BackgroundJobs:MeshCentralGroupPolicyReconciliationEnabled") ?? true);
    }

    public static IServiceCollection AddDiscoveryBackgroundServices(
        this IServiceCollection services,
        BackgroundServicesConfig config)
    {
        // Observability registry — shared by every IHostedService and the
        // BackgroundServicesController dashboard.
        services.AddSingleton<BackgroundServiceRegistry>();

        // Always-registered services
        services.AddHostedService<AlertSchedulerBackgroundService>();
        services.AddHostedService<AgentPackagePrebuildHostedService>();

        // Sync ping dispatch (singleton + hosted service pattern)
        services.AddSingleton<ISyncPingDispatchQueue, SyncPingDispatchBackgroundService>();
        services.AddHostedService(sp => (SyncPingDispatchBackgroundService)sp.GetRequiredService<ISyncPingDispatchQueue>());

        // Note: LogPurge, ReportRetention, AiChatRetention, and P2pMaintenance
        // have been migrated to Quartz.NET jobs. See QuartzServiceCollectionExtensions.

        // Knowledge Base
        if (config.KnowledgeEmbeddingEnabled)
        {
            services.AddHostedService<KnowledgeEmbeddingBackgroundService>();
        }

        if (config.KnowledgeEmbeddingQueueEnabled)
        {
            services.AddHostedService<KnowledgeEmbeddingQueueBackgroundService>();
        }

        // SLA
        if (config.SlaMonitoringEnabled)
        {
            services.AddHostedService<SlaMonitoringBackgroundService>();
        }

        // Reports
        if (config.ReportGenerationEnabled)
        {
            services.AddHostedService<ReportGenerationBackgroundService>();
        }

        // Non-development only services (reconciliations)
        if (!config.IsDevelopment)
        {
            if (config.AgentLabelingReconciliationEnabled)
            {
                services.AddHostedService<AgentLabelingReconciliationBackgroundService>();
            }

            if (config.MeshCentralIdentityReconciliationEnabled)
            {
                services.AddHostedService<MeshCentralIdentityReconciliationBackgroundService>();
            }

            if (config.MeshCentralGroupPolicyReconciliationEnabled)
            {
                services.AddHostedService<MeshCentralGroupPolicyReconciliationBackgroundService>();
            }
        }

        return services;
    }
}
