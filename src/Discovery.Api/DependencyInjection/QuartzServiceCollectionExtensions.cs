using Discovery.Api.Services.Quartz;
using Quartz;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers Quartz.NET scheduler with in-memory store (Phase 1).
/// PostgreSQL persistence can be enabled later for clustering support.
/// BackgroundJobs:*Enabled toggles from appsettings.json control which jobs are scheduled.
/// </summary>
public static class QuartzServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryQuartz(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddQuartz(q =>
        {
            q.SchedulerName = "Discovery-RMM-Scheduler";
            q.SchedulerId = "AUTO";

            // ── Log Purge: daily at 3 AM ────────────────────────────────
            q.ScheduleJob<LogPurgeJob>(trigger => trigger
                .WithIdentity($"{LogPurgeJob.Key.Name}-trigger", LogPurgeJob.Key.Group)
                .WithCronSchedule("0 0 3 * * ?")
                .WithDescription("Purge old log entries"));

            // ── Report Retention: daily at 4 AM ──────────────────────────
            q.ScheduleJob<ReportRetentionJob>(trigger => trigger
                .WithIdentity($"{ReportRetentionJob.Key.Name}-trigger", ReportRetentionJob.Key.Group)
                .WithCronSchedule("0 0 4 * * ?")
                .WithDescription("Purge old report executions and files"));

            // ── AI Chat Retention: daily at 2 AM ─────────────────────────
            q.ScheduleJob<AiChatRetentionJob>(trigger => trigger
                .WithIdentity($"{AiChatRetentionJob.Key.Name}-trigger", AiChatRetentionJob.Key.Group)
                .WithCronSchedule("0 0 2 * * ?")
                .WithDescription("Soft/hard delete expired AI chat sessions"));

            // ── P2P Maintenance: every 15 minutes ────────────────────────
            q.ScheduleJob<P2pMaintenanceJob>(trigger => trigger
                .WithIdentity($"{P2pMaintenanceJob.Key.Name}-trigger", P2pMaintenanceJob.Key.Group)
                .WithSimpleSchedule(s => s.WithIntervalInMinutes(15).RepeatForever())
                .WithDescription("Clean stale P2P presence and recalculate seed plans"));

            // ── Knowledge Embedding: every 30 seconds ────────────────────
            q.ScheduleJob<KnowledgeEmbeddingJob>(trigger => trigger
                .WithIdentity($"{KnowledgeEmbeddingJob.Key.Name}-trigger", KnowledgeEmbeddingJob.Key.Group)
                .WithSimpleSchedule(s => s.WithIntervalInSeconds(30).RepeatForever())
                .WithDescription("Re-chunk articles, generate embeddings in batch, process LISTEN/NOTIFY queue"));

            // ── Winget Catalog Sync: every N days at midnight ───────────
            var wingetEnabled = configuration.GetValue<bool?>("BackgroundJobs:WingetCatalogSync:Enabled") ?? false;
            var wingetIntervalDays = Math.Max(1, configuration.GetValue<int?>("BackgroundJobs:WingetCatalogSync:IntervalDays") ?? 5);

            if (wingetEnabled)
            {
                var wingetCron = $"0 0 0 */{wingetIntervalDays} * ?";
                q.ScheduleJob<WingetCatalogSyncJob>(trigger => trigger
                    .WithIdentity($"{WingetCatalogSyncJob.Key.Name}-trigger", WingetCatalogSyncJob.Key.Group)
                    .WithCronSchedule(wingetCron)
                    .WithDescription($"Sync Winget package catalog every {wingetIntervalDays} day(s) at midnight"));
            }

            // ── Alert Scheduler: every 30 seconds ─────────────────────
            q.ScheduleJob<AlertSchedulerJob>(trigger => trigger
                .WithIdentity($"{AlertSchedulerJob.Key.Name}-trigger", AlertSchedulerJob.Key.Group)
                .WithSimpleSchedule(s => s.WithIntervalInSeconds(30).RepeatForever())
                .WithDescription("Dispatch pending scheduled alerts and expire stale ones"));

            // ── SLA Monitoring: every 5 minutes ────────────────────────
            q.ScheduleJob<SlaMonitoringJob>(trigger => trigger
                .WithIdentity($"{SlaMonitoringJob.Key.Name}-trigger", SlaMonitoringJob.Key.Group)
                .WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever())
                .WithDescription("Check SLA for open tickets and trigger escalations"));

            // ── Report Generation: every 15 seconds ────────────────────
            q.ScheduleJob<ReportGenerationJob>(trigger => trigger
                .WithIdentity($"{ReportGenerationJob.Key.Name}-trigger", ReportGenerationJob.Key.Group)
                .WithSimpleSchedule(s => s.WithIntervalInSeconds(15).RepeatForever())
                .WithDescription("Process pending report executions"));

            // ── Report Schedule Dispatch: every 60 seconds ────────────
            q.ScheduleJob<ReportScheduleDispatchJob>(trigger => trigger
                .WithIdentity($"{ReportScheduleDispatchJob.Key.Name}-trigger", ReportScheduleDispatchJob.Key.Group)
                .WithSimpleSchedule(s => s.WithIntervalInSeconds(60).RepeatForever())
                .WithDescription("Dispatch due scheduled report generations"));

            // ── Data Retention: daily at 3:30 AM ──────────────────────
            q.ScheduleJob<DataRetentionJob>(trigger => trigger
                .WithIdentity($"{DataRetentionJob.Key.Name}-trigger", DataRetentionJob.Key.Group)
                .WithCronSchedule("0 30 3 * * ?")
                .WithDescription("Purge old sessions, tokens, notifications, commands, telemetry"));

            // ── Database Maintenance: weekly Sunday 3 AM ───────────────
            q.ScheduleJob<DatabaseMaintenanceJob>(trigger => trigger
                .WithIdentity($"{DatabaseMaintenanceJob.Key.Name}-trigger", DatabaseMaintenanceJob.Key.Group)
                .WithCronSchedule("0 0 3 ? * SUN")
                .WithDescription("VACUUM, REINDEX, ANALYZE on configured tables"));

            // ── Agent Labeling Reconciliation: every 10 minutes ────────
            q.ScheduleJob<AgentLabelingReconciliationJob>(trigger => trigger
                .WithIdentity($"{AgentLabelingReconciliationJob.Key.Name}-trigger", AgentLabelingReconciliationJob.Key.Group)
                .WithSimpleSchedule(s => s.WithIntervalInMinutes(10).RepeatForever())
                .WithDescription("Reconcile agent labels with auto-labeling rules"));

            // ── MeshCentral Identity Reconciliation: hourly ────────────
            q.ScheduleJob<MeshCentralIdentityReconciliationJob>(trigger => trigger
                .WithIdentity($"{MeshCentralIdentityReconciliationJob.Key.Name}-trigger", MeshCentralIdentityReconciliationJob.Key.Group)
                .WithCronSchedule("0 0 * * * ?")
                .WithDescription("Backfill MeshCentral identity sync"));

            // ── MeshCentral Group Policy Reconciliation: hourly ────────
            q.ScheduleJob<MeshCentralGroupPolicyReconciliationJob>(trigger => trigger
                .WithIdentity($"{MeshCentralGroupPolicyReconciliationJob.Key.Name}-trigger", MeshCentralGroupPolicyReconciliationJob.Key.Group)
                .WithCronSchedule("0 30 * * * ?")
                .WithDescription("Backfill MeshCentral group policy sync"));
        });

        services.AddQuartzHostedService(options =>
        {
            options.WaitForJobsToComplete = true;
            options.AwaitApplicationStarted = true;
        });

        // Register the global job execution history listener
        services.AddSingleton<JobExecutionHistoryListener>();
        services.Configure<QuartzOptions>(options =>
        {
            // Listener will be wired via scheduler.ListenerManager in startup
        });

        return services;
    }

    /// <summary>
    /// Wires the job listener to the scheduler at application startup.
    /// Call this from Program.cs after building the app.
    /// </summary>
    public static async Task WireJobListenerAsync(IServiceProvider services)
    {
        var schedulerFactory = services.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler();

        // Expose the root IServiceProvider so jobs can resolve scoped services
        // via JobExecutionContextExtensions.GetScopedService<T>().
        scheduler.Context.Put(JobExecutionContextExtensions.ServiceProviderKey, services);

        var listener = services.GetRequiredService<JobExecutionHistoryListener>();
        scheduler.ListenerManager.AddJobListener(listener, Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup());
    }
}
