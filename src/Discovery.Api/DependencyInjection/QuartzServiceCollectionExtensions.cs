using Discovery.Api.Services.Quartz;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
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
            q.ConfigureScheduler(o =>
            {
                o.InstanceName = "Discovery-RMM-Scheduler";
                o.InstanceId = "AUTO";
            });

            // ── Log Purge: daily at 3 AM ────────────────────────────────
            q.ScheduleJob<LogPurgeJob>(trigger => trigger
                .WithIdentity(LogPurgeJob.Key.Name, LogPurgeJob.Key.Group)
                .WithCronSchedule("0 0 3 * * ?")
                .WithDescription("Purge old log entries"));

            // ── Report Retention: daily at 4 AM ──────────────────────────
            q.ScheduleJob<ReportRetentionJob>(trigger => trigger
                .WithIdentity(ReportRetentionJob.Key.Name, ReportRetentionJob.Key.Group)
                .WithCronSchedule("0 0 4 * * ?")
                .WithDescription("Purge old report executions and files"));

            // ── AI Chat Retention: daily at 2 AM ─────────────────────────
            q.ScheduleJob<AiChatRetentionJob>(trigger => trigger
                .WithIdentity(AiChatRetentionJob.Key.Name, AiChatRetentionJob.Key.Group)
                .WithCronSchedule("0 0 2 * * ?")
                .WithDescription("Soft/hard delete expired AI chat sessions"));

            // ── Ticket Support Chat: every 1 minute ──────────────────────
            q.ScheduleJob<TicketChatMaintenanceJob>(trigger => trigger
                .WithIdentity(TicketChatMaintenanceJob.Key.Name, TicketChatMaintenanceJob.Key.Group)
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(20))
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(1)).RepeatForever())
                .WithDescription("Expire chat invites, close inactive/ticket-closed support chat sessions"));

            // ── P2P Maintenance: every 15 minutes ────────────────────────
            q.ScheduleJob<P2pMaintenanceJob>(trigger => trigger
                .WithIdentity(P2pMaintenanceJob.Key.Name, P2pMaintenanceJob.Key.Group)
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(15)).RepeatForever())
                .WithDescription("Clean stale P2P presence and recalculate seed plans"));

            // ── Knowledge Embedding: every 30 seconds ────────────────────
            q.ScheduleJob<KnowledgeEmbeddingJob>(trigger => trigger
                .WithIdentity(KnowledgeEmbeddingJob.Key.Name, KnowledgeEmbeddingJob.Key.Group)
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(30)).RepeatForever())
                .WithDescription("Re-chunk articles, generate embeddings in batch, process LISTEN/NOTIFY queue"));

            // ── Embeddings das respostas do questionário (busca semântica): a cada N segundos ──
            var answerEmbeddingEnabled = configuration.GetValue<bool?>("BackgroundJobs:TicketAnswerEmbedding:Enabled") ?? true;
            if (answerEmbeddingEnabled)
            {
                // Piso de 15s: o processador tem teto por ciclo (50 respostas) e o
                // custo ocioso é uma consulta indexada (índice parcial).
                var answerEmbeddingIntervalSeconds = Math.Max(15, configuration.GetValue<int?>("BackgroundJobs:TicketAnswerEmbedding:IntervalSeconds") ?? 60);
                // Catch-up pós restart/deploy: primeira execução logo após o startup.
                var answerEmbeddingStartupDelaySeconds = Math.Max(0, configuration.GetValue<int?>("BackgroundJobs:TicketAnswerEmbedding:StartupDelaySeconds") ?? 20);

                q.ScheduleJob<TicketAnswerEmbeddingJob>(trigger => trigger
                    .WithIdentity(TicketAnswerEmbeddingJob.Key.Name, TicketAnswerEmbeddingJob.Key.Group)
                    .StartAt(DateTimeOffset.UtcNow.AddSeconds(answerEmbeddingStartupDelaySeconds))
                    .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(answerEmbeddingIntervalSeconds)).RepeatForever())
                    .WithDescription($"Generate ticket answer embeddings {answerEmbeddingStartupDelaySeconds}s after startup, then every {answerEmbeddingIntervalSeconds}s"));
            }

            // ── Winget Catalog Sync (feed legado): every N days at midnight ───────────
            var wingetEnabled = configuration.GetValue<bool?>("BackgroundJobs:WingetCatalogSync:Enabled") ?? false;
            var wingetIntervalDays = Math.Max(1, configuration.GetValue<int?>("BackgroundJobs:WingetCatalogSync:IntervalDays") ?? 5);

            if (wingetEnabled)
            {
                var wingetCron = $"0 0 0 */{wingetIntervalDays} * ?";
                q.ScheduleJob<WingetCatalogSyncJob>(trigger => trigger
                    .WithIdentity(WingetCatalogSyncJob.Key.Name, WingetCatalogSyncJob.Key.Group)
                    .WithCronSchedule(wingetCron)
                    .WithDescription($"Sync Winget package catalog every {wingetIntervalDays} day(s) at midnight"));
            }

            // ── Winget Manifests Sync (shallow clone winget-pkgs, fonte primária): startup + a cada N minutos ──
            var manifestsEnabled = configuration.GetValue<bool?>("AppCatalog:Winget:Enabled") ?? true;
            var manifestsSource = configuration.GetValue<string?>("AppCatalog:Winget:Source") ?? "manifests";
            // Cadência default: 6h. Piso de 15 min protege contra config agressiva
            // (clone/pull + import são custosos para rodar em loop curto).
            var manifestsIntervalMin = Math.Max(15, configuration.GetValue<int?>("AppCatalog:Winget:ManifestsPollIntervalMinutes") ?? 360);
            // Primeira execução logo após o startup (catch-up pós restart/deploy); 0 = no start do scheduler.
            var manifestsStartupDelayMin = Math.Max(0, configuration.GetValue<int?>("AppCatalog:Winget:StartupSyncDelayMinutes") ?? 2);

            if (manifestsEnabled && !manifestsSource.Equals("feed", StringComparison.OrdinalIgnoreCase))
            {
                q.ScheduleJob<WingetManifestsSyncJob>(trigger => trigger
                    .WithIdentity(WingetManifestsSyncJob.Key.Name, WingetManifestsSyncJob.Key.Group)
                    .StartAt(DateTimeOffset.UtcNow.AddMinutes(manifestsStartupDelayMin))
                    .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(manifestsIntervalMin)).RepeatForever())
                    .WithDescription($"Sync Winget catalog from manifests clone {manifestsStartupDelayMin} min after startup, then every {manifestsIntervalMin} minute(s)"));
            }

            // ── Alert Scheduler: every 30 seconds ─────────────────────
            q.ScheduleJob<AlertSchedulerJob>(trigger => trigger
                .WithIdentity(AlertSchedulerJob.Key.Name, AlertSchedulerJob.Key.Group)
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(30)).RepeatForever())
                .WithDescription("Dispatch pending scheduled alerts and expire stale ones"));

            // ── SLA Monitoring: every 5 minutes ────────────────────────
            q.ScheduleJob<SlaMonitoringJob>(trigger => trigger
                .WithIdentity(SlaMonitoringJob.Key.Name, SlaMonitoringJob.Key.Group)
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(5)).RepeatForever())
                .WithDescription("Check SLA for open tickets and trigger escalations"));

            // ── Report Generation: every 15 seconds ────────────────────
            q.ScheduleJob<ReportGenerationJob>(trigger => trigger
                .WithIdentity(ReportGenerationJob.Key.Name, ReportGenerationJob.Key.Group)
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(15)).RepeatForever())
                .WithDescription("Process pending report executions"));

            // ── Report Schedule Dispatch: every 60 seconds ────────────
            q.ScheduleJob<ReportScheduleDispatchJob>(trigger => trigger
                .WithIdentity(ReportScheduleDispatchJob.Key.Name, ReportScheduleDispatchJob.Key.Group)
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(60)).RepeatForever())
                .WithDescription("Dispatch due scheduled report generations"));

            // ── Data Retention: daily at 3:30 AM ──────────────────────
            q.ScheduleJob<DataRetentionJob>(trigger => trigger
                .WithIdentity(DataRetentionJob.Key.Name, DataRetentionJob.Key.Group)
                .WithCronSchedule("0 30 3 * * ?")
                .WithDescription("Purge old sessions, tokens, notifications, commands, telemetry"));

            // ── Database Maintenance: weekly Sunday 3 AM ───────────────
            q.ScheduleJob<DatabaseMaintenanceJob>(trigger => trigger
                .WithIdentity(DatabaseMaintenanceJob.Key.Name, DatabaseMaintenanceJob.Key.Group)
                .WithCronSchedule("0 0 3 ? * SUN")
                .WithDescription("VACUUM, REINDEX, ANALYZE, FREEZE conforme DatabaseMaintenanceSettings"));

            // ── Agent Labeling Reconciliation: every 10 minutes ────────
            q.ScheduleJob<AgentLabelingReconciliationJob>(trigger => trigger
                .WithIdentity(AgentLabelingReconciliationJob.Key.Name, AgentLabelingReconciliationJob.Key.Group)
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(10)).RepeatForever())
                .WithDescription("Reconcile agent labels with auto-labeling rules"));

            // ── Triagem por IA na auto-atribuição: a cada N segundos ───
            var aiAssignmentEnabled = configuration.GetValue<bool?>("BackgroundJobs:AiTicketAssignment:Enabled") ?? true;
            if (aiAssignmentEnabled)
            {
                // Piso de 10s: o processador tem teto por ciclo (BatchSize) e o
                // custo ocioso é uma consulta indexada na fila.
                var aiAssignmentIntervalSeconds = Math.Max(10, configuration.GetValue<int?>("BackgroundJobs:AiTicketAssignment:IntervalSeconds") ?? 20);
                var aiAssignmentStartupDelaySeconds = Math.Max(0, configuration.GetValue<int?>("BackgroundJobs:AiTicketAssignment:StartupDelaySeconds") ?? 20);

                q.ScheduleJob<AiTicketAssignmentJob>(trigger => trigger
                    .WithIdentity(AiTicketAssignmentJob.Key.Name, AiTicketAssignmentJob.Key.Group)
                    .StartAt(DateTimeOffset.UtcNow.AddSeconds(aiAssignmentStartupDelaySeconds))
                    .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(aiAssignmentIntervalSeconds)).RepeatForever())
                    .WithDescription("Processa a fila da triagem por IA e aplica o fallback de chamados sem responsável"));
            }

            // ── Métricas por atendente: a cada N minutos ───────────────
            var metricsRefreshEnabled = configuration.GetValue<bool?>("BackgroundJobs:TechnicianMetrics:Enabled") ?? true;
            if (metricsRefreshEnabled)
            {
                // Tick é a granularidade MÍNIMA de varredura; o intervalo efetivo por
                // cliente vem da configuração (piso de 10 min) e é verificado no ciclo.
                var metricsTickSeconds = Math.Max(60, configuration.GetValue<int?>("BackgroundJobs:TechnicianMetrics:TickSeconds") ?? 300);
                var metricsStartupDelaySeconds = Math.Max(0, configuration.GetValue<int?>("BackgroundJobs:TechnicianMetrics:StartupDelaySeconds") ?? 60);

                q.ScheduleJob<TechnicianMetricsRefreshJob>(trigger => trigger
                    .WithIdentity(TechnicianMetricsRefreshJob.Key.Name, TechnicianMetricsRefreshJob.Key.Group)
                    .StartAt(DateTimeOffset.UtcNow.AddSeconds(metricsStartupDelaySeconds))
                    .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(metricsTickSeconds)).RepeatForever())
                    .WithDescription("Atualiza, por escopo de cliente vencido, os snapshots de métricas usados pela triagem por IA"));
            }

            // ── Aprendizado da triagem por IA: diário ───────────────────
            var learningEnabled = configuration.GetValue<bool?>("BackgroundJobs:AiAssignmentLearning:Enabled") ?? true;
            if (learningEnabled)
            {
                var learningHour = Math.Clamp(configuration.GetValue<int?>("BackgroundJobs:AiAssignmentLearning:HourUtc") ?? 4, 0, 23);

                q.ScheduleJob<AiAssignmentLearningJob>(trigger => trigger
                    .WithIdentity(AiAssignmentLearningJob.Key.Name, AiAssignmentLearningJob.Key.Group)
                    .WithCronSchedule($"0 30 {learningHour} * * ?")
                    .WithDescription("Ciclo de aprendizado da triagem por IA (competências e pesos)"));
            }

            // ── Backfill de snapshots de métricas: a cada N segundos ───
            var backfillEnabled = configuration.GetValue<bool?>("BackgroundJobs:TechnicianMetricsBackfill:Enabled") ?? true;
            if (backfillEnabled)
            {
                var backfillIntervalSeconds = Math.Max(10, configuration.GetValue<int?>("BackgroundJobs:TechnicianMetricsBackfill:IntervalSeconds") ?? 30);

                q.ScheduleJob<TechnicianMetricsBackfillJob>(trigger => trigger
                    .WithIdentity(TechnicianMetricsBackfillJob.Key.Name, TechnicianMetricsBackfillJob.Key.Group)
                    .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(backfillIntervalSeconds)).RepeatForever())
                    .WithDescription("Processa em lotes o backfill (recálculo forçado) dos snapshots de métricas"));
            }

            // ── Gerenciador de Patches (Windows Update) ────────────────
            var patchAutoInstallEnabled = configuration.GetValue<bool?>("BackgroundJobs:PatchAutoInstall:Enabled") ?? true;
            if (patchAutoInstallEnabled)
            {
                var patchAutoInstallIntervalMinutes = Math.Max(5, configuration
                    .GetValue<int?>("BackgroundJobs:PatchAutoInstall:IntervalMinutes") ?? 15);

                q.ScheduleJob<PatchAutoInstallJob>(trigger => trigger
                    .WithIdentity(PatchAutoInstallJob.Key.Name, PatchAutoInstallJob.Key.Group)
                    .WithSimpleSchedule(s => s
                        .WithInterval(TimeSpan.FromMinutes(patchAutoInstallIntervalMinutes))
                        .RepeatForever())
                    .WithDescription("Instala patches aprovados dentro da janela de manutenção (opt-in por política)"));
            }

            var patchRebootNoticeEnabled = configuration.GetValue<bool?>("BackgroundJobs:PatchRebootNotice:Enabled") ?? true;
            if (patchRebootNoticeEnabled)
            {
                var patchRebootNoticeIntervalMinutes = Math.Max(5, configuration
                    .GetValue<int?>("BackgroundJobs:PatchRebootNotice:IntervalMinutes") ?? 15);

                q.ScheduleJob<PatchRebootNoticeJob>(trigger => trigger
                    .WithIdentity(PatchRebootNoticeJob.Key.Name, PatchRebootNoticeJob.Key.Group)
                    .WithSimpleSchedule(s => s
                        .WithInterval(TimeSpan.FromMinutes(patchRebootNoticeIntervalMinutes))
                        .RepeatForever())
                    .WithDescription("Consome a política de reboot para agentes com reinício pendente (nunca reinicia)"));
            }

            var patchMaintenanceEnabled = configuration.GetValue<bool?>("BackgroundJobs:PatchMaintenance:Enabled") ?? true;
            if (patchMaintenanceEnabled)
            {
                var patchMaintenanceHour = Math.Clamp(configuration
                    .GetValue<int?>("BackgroundJobs:PatchMaintenance:HourUtc") ?? 4, 0, 23);

                q.ScheduleJob<PatchMaintenanceJob>(trigger => trigger
                    .WithIdentity(PatchMaintenanceJob.Key.Name, PatchMaintenanceJob.Key.Group)
                    .WithCronSchedule($"0 15 {patchMaintenanceHour} * * ?")
                    .WithDescription("Expira instalações de patch sem resultado e reconcilia aprovações"));
            }

            // ── Sincronização do agendamento (tick vindo do banco) ─────
            var scheduleSyncEnabled = configuration.GetValue<bool?>("BackgroundJobs:BackgroundProcessingScheduleSync:Enabled") ?? true;
            if (scheduleSyncEnabled)
            {
                var scheduleSyncIntervalSeconds = Math.Max(30, configuration.GetValue<int?>("BackgroundJobs:BackgroundProcessingScheduleSync:IntervalSeconds") ?? 120);

                q.ScheduleJob<BackgroundProcessingScheduleSyncJob>(trigger => trigger
                    .WithIdentity(BackgroundProcessingScheduleSyncJob.Key.Name, BackgroundProcessingScheduleSyncJob.Key.Group)
                    .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(scheduleSyncIntervalSeconds)).RepeatForever())
                    .WithDescription("Sincroniza o tick dos processamentos em segundo plano com a configuração global"));
            }
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
    /// Aplica o cron de <c>DatabaseMaintenanceSettings.Schedule</c> ao trigger do
    /// job de manutenção. Sem isso o horário configurado no banco era ignorado
    /// (o trigger ficava sempre no default "0 0 3 ? * SUN").
    /// </summary>
    public static async Task ApplyDatabaseMaintenanceScheduleAsync(IServiceProvider services)
    {
        var scheduler = await services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        var jobKey = DatabaseMaintenanceJob.Key;

        if (!await scheduler.Exists(jobKey))
            return;

        await using var scope = services.CreateAsyncScope();
        var serverRepo = scope.ServiceProvider.GetRequiredService<IServerConfigurationRepository>();
        var server = await serverRepo.GetOrCreateDefaultAsync();
        var settings = DatabaseMaintenanceSettings.Parse(server.RetentionSettingsJson);

        var existing = (await scheduler.GetTriggersOfJob(jobKey)).Select(t => t.Key).FirstOrDefault();
        var triggerKey = existing ?? new TriggerKey(jobKey.Name, jobKey.Group);
        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(settings.Schedule)
            .WithDescription("VACUUM, REINDEX, ANALYZE, FREEZE conforme DatabaseMaintenanceSettings")
            .Build();

        if (existing is null)
            await scheduler.ScheduleJob(trigger);
        else
            await scheduler.RescheduleJob(triggerKey, trigger);
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
        scheduler.Context[JobExecutionContextExtensions.ServiceProviderKey] = services;

        var listener = services.GetRequiredService<JobExecutionHistoryListener>();
        scheduler.ListenerManager.AddJobListener(listener, Quartz.GroupMatcher<JobKey>.AnyGroup());
    }
}
