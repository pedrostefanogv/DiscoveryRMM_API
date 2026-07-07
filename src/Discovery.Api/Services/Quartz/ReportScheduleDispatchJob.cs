using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job that dispatches due scheduled report generations.
/// Evaluates cron expressions and creates report executions for schedules
/// whose next trigger time has arrived.
/// Schedule: every 60 seconds.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ReportScheduleDispatchJob : IJob
{
    public static readonly JobKey Key = new("report-schedule-dispatch", "reports");
    private const int BatchSize = 50;

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<ReportScheduleDispatchJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var scheduleRepo = scope.ServiceProvider.GetRequiredService<IReportScheduleRepository>();
        var templateRepo = scope.ServiceProvider.GetRequiredService<IReportTemplateRepository>();
        var executionRepo = scope.ServiceProvider.GetRequiredService<IReportExecutionRepository>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        try
        {
            var utcNow = DateTime.UtcNow;
            var dueSchedules = await scheduleRepo.GetDueSchedulesAsync(utcNow, BatchSize);

            if (dueSchedules.Count == 0)
                return;

            logger.LogInformation("Found {Count} due report schedules to process.", dueSchedules.Count);

            foreach (var schedule in dueSchedules)
            {
                ct.ThrowIfCancellationRequested();

                // Resolve dynamic filters (e.g. <now-24h>)
                var resolvedFilters = ReportParameterResolver.ResolveFiltersJson(schedule.FiltersJson, utcNow);

                // Create execution
                var execution = new ReportExecution
                {
                    TemplateId = schedule.TemplateId,
                    ClientId = schedule.ClientId,
                    Format = schedule.Format,
                    FiltersJson = resolvedFilters,
                    Status = ReportExecutionStatus.Pending,
                    CreatedBy = schedule.CreatedBy ?? "ReportSchedule",
                    ScheduleId = schedule.Id
                };

                await executionRepo.CreateAsync(execution);

                // Compute next trigger time from cron expression
                var nextTrigger = ComputeNextTriggerUtc(schedule.CronExpression, utcNow);
                schedule.LastTriggeredAt = utcNow;
                schedule.NextTriggerAt = nextTrigger;
                await scheduleRepo.UpdateAsync(schedule);

                logger.LogInformation(
                    "Schedule {ScheduleId} ({Label}) triggered execution {ExecutionId} (next: {NextTrigger:O}, delivery: {DeliveryMode})",
                    schedule.Id, schedule.ScheduleLabel, execution.Id, nextTrigger, schedule.DeliveryMode);

                // Fire-and-forget delivery notification (will be handled after execution completes)
                if (!string.IsNullOrWhiteSpace(schedule.DeliveryMode) &&
                    !string.Equals(schedule.DeliveryMode, "storage", StringComparison.OrdinalIgnoreCase))
                {
                    _ = notificationService.PublishAsync(new NotificationPublishRequest(
                        EventType: "report.schedule.triggered",
                        Topic: "report-delivery",
                        Title: $"Relatorio agendado: {schedule.ScheduleLabel ?? "Scheduled Report"}",
                        Message: $"Execucao #{execution.Id} iniciada. Delivery: {schedule.DeliveryMode}. Download disponivel quando concluida.",
                        Payload: new
                        {
                            executionId = execution.Id,
                            scheduleId = schedule.Id,
                            deliveryMode = schedule.DeliveryMode
                        }));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error while dispatching scheduled report generations.");
        }
    }

    /// <summary>
    /// Computes the next trigger time from a Quartz-compatible cron expression.
    /// </summary>
    private static DateTime? ComputeNextTriggerUtc(string cronExpression, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
            return null;

        try
        {
            var cron = new CronExpression(cronExpression);
            var next = cron.GetNextValidTimeAfter(utcNow);
            return next?.UtcDateTime;
        }
        catch
        {
            return null;
        }
    }
}
