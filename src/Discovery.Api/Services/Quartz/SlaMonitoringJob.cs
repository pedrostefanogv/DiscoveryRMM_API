using System.Collections.Concurrent;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que monitora SLAs de tickets e marca violações.
/// Substitui SlaMonitoringBackgroundService.
/// Schedule: a cada 5 minutos.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SlaMonitoringJob : IJob
{
    public static readonly JobKey Key = new("sla-monitoring", "alerts");

    private static readonly ConcurrentDictionary<Guid, DateTime> WarningLoggedAtUtc = new();
    private const int WarningCooldownMinutes = 30;

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<SlaMonitoringJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var ticketRepo = scope.ServiceProvider.GetRequiredService<ITicketRepository>();
        var slaService = scope.ServiceProvider.GetRequiredService<ISlaService>();
        var activityLogService = scope.ServiceProvider.GetRequiredService<IActivityLogService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var escalationRuleRepo = scope.ServiceProvider.GetRequiredService<ITicketEscalationRuleRepository>();

        var openTickets = await ticketRepo.GetOpenTicketsWithSlaAsync();
        if (openTickets == null || !openTickets.Any())
        {
            logger.LogDebug("No open tickets with SLA to check");
            return;
        }

        logger.LogInformation("Checking SLA for {Count} open tickets", openTickets.Count);

        foreach (var ticket in openTickets)
        {
            if (ct.IsCancellationRequested) break;

            var breached = await slaService.CheckAndLogSlaBreachAsync(ticket.Id);
            if (breached)
            {
                logger.LogWarning("SLA Breached: Ticket {TicketId} - {Title}", ticket.Id, ticket.Title);

                if (ticket.AssignedToUserId.HasValue)
                {
                    await notificationService.PublishAsync(new NotificationPublishRequest(
                        EventType: "ticket.sla_breached",
                        Topic: "tickets",
                        Title: "SLA violado",
                        Message: $"O SLA do ticket '{ticket.Title}' foi violado.",
                        Severity: NotificationSeverity.Critical,
                        Payload: new { ticketId = ticket.Id },
                        RecipientUserId: ticket.AssignedToUserId
                    ), ct);
                }
            }
            else
            {
                var (_, percentUsed, _) = await slaService.GetSlaStatusAsync(ticket.Id);

                if (percentUsed >= 80 && percentUsed < 85)
                {
                    if (ShouldLogWarning(ticket.Id))
                    {
                        await activityLogService.LogActivityAsync(
                            ticket.Id, TicketActivityType.SlaWarning, null,
                            percentUsed.ToString("F2"), "80", "SLA warning: 20% time remaining");

                        if (ticket.AssignedToUserId.HasValue)
                        {
                            await notificationService.PublishAsync(new NotificationPublishRequest(
                                EventType: "ticket.sla_warning",
                                Topic: "tickets",
                                Title: "Aviso de SLA",
                                Message: $"O ticket '{ticket.Title}' utilizou {percentUsed:F0}% do tempo de SLA.",
                                Severity: NotificationSeverity.Warning,
                                Payload: new { ticketId = ticket.Id, percentUsed },
                                RecipientUserId: ticket.AssignedToUserId
                            ), ct);
                        }

                        logger.LogWarning("SLA Warning: Ticket {TicketId} - {Percent}% used",
                            ticket.Id, percentUsed.ToString("F2"));
                    }
                }
                else
                {
                    WarningLoggedAtUtc.TryRemove(ticket.Id, out _);
                }

                await ProcessEscalationRulesAsync(
                    ticket, percentUsed, slaService, escalationRuleRepo,
                    notificationService, ticketRepo, logger, ct);
            }
        }
    }

    private static bool ShouldLogWarning(Guid ticketId)
    {
        if (WarningLoggedAtUtc.TryGetValue(ticketId, out var lastWarning))
        {
            if (DateTime.UtcNow - lastWarning < TimeSpan.FromMinutes(WarningCooldownMinutes))
                return false;
        }
        WarningLoggedAtUtc[ticketId] = DateTime.UtcNow;
        return true;
    }

    private static async Task ProcessEscalationRulesAsync(
        Discovery.Core.Entities.Ticket ticket,
        double slaPercentUsed,
        ISlaService slaService,
        ITicketEscalationRuleRepository escalationRuleRepo,
        INotificationService notificationService,
        ITicketRepository ticketRepo,
        ILogger logger,
        CancellationToken ct)
    {
        if (!ticket.WorkflowProfileId.HasValue) return;

        var rules = await escalationRuleRepo.GetByWorkflowProfileIdAsync(ticket.WorkflowProfileId.Value);
        var now = DateTime.UtcNow;

        foreach (var rule in rules.Where(r => r.IsActive))
        {
            bool shouldFire = false;

            if (rule.TriggerAtSlaPercent > 0 && slaPercentUsed >= rule.TriggerAtSlaPercent)
                shouldFire = true;

            if (!shouldFire && rule.TriggerAtHoursBefore > 0 && ticket.SlaExpiresAt.HasValue)
            {
                var effectiveExpiry = slaService.GetEffectiveSlaExpiry(ticket);
                if (effectiveExpiry.HasValue && (effectiveExpiry.Value - now).TotalHours <= rule.TriggerAtHoursBefore)
                    shouldFire = true;
            }

            if (!shouldFire) continue;

            // Bump priority
            if (rule.BumpPriority && ticket.Priority < Discovery.Core.Enums.TicketPriority.Critical)
            {
                ticket.Priority = (Discovery.Core.Enums.TicketPriority)((int)ticket.Priority + 1);
                await ticketRepo.UpdateAsync(ticket);
                logger.LogInformation("Escalation: bumped priority of ticket {TicketId} to {Priority}", ticket.Id, ticket.Priority);
            }

            // Notify
            if (rule.NotifyAssignee && ticket.AssignedToUserId.HasValue)
            {
                await notificationService.PublishAsync(new NotificationPublishRequest(
                    EventType: "ticket.escalated",
                    Topic: "tickets",
                    Title: "Ticket escalado",
                    Message: $"O ticket '{ticket.Title}' foi escalado pela regra '{rule.Name}'.",
                    Severity: NotificationSeverity.Warning,
                    Payload: new { ticketId = ticket.Id, ruleId = rule.Id },
                    RecipientUserId: ticket.AssignedToUserId
                ), ct);
            }
        }
    }
}
