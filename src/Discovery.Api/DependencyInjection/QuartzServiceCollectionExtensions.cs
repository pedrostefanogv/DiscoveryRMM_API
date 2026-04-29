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
