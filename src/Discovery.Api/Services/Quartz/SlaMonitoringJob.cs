using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que monitora SLAs de tickets e marca violações.
/// Schedule: a cada 5 minutos.
/// Usa lock distribuído (Redis) para evitar execução duplicada em multi-instância.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SlaMonitoringJob : IJob
{
    public static readonly JobKey Key = new("sla-monitoring", "alerts");

    private const int WarningCooldownMinutes = 30;
    private const int MaxConcurrentChecks = 8;
    private const string LockKey = "locks:sla-monitoring";
    private const int LockTtlSeconds = 240; // 4 min (job roda a cada 5 min)

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
        var redis = scope.ServiceProvider.GetRequiredService<IRedisService>();

        // Lock distribuído para evitar execução simultânea em multi-instância
        var acquired = await redis.SetIfNotExistsAsync(LockKey, Environment.MachineName, LockTtlSeconds);
        if (!acquired)
        {
            logger.LogDebug("SLA monitoring lock not acquired (another instance is running)");
            return;
        }

        try
        {
            var openTickets = await ticketRepo.GetOpenTicketsWithSlaAsync();
            if (openTickets == null || !openTickets.Any())
            {
                logger.LogDebug("No open tickets with SLA to check");
                return;
            }

            logger.LogInformation("Checking SLA for {Count} open tickets", openTickets.Count);

            // Processamento paralelo com grau limitado
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentChecks,
                CancellationToken = ct
            };

            var ticketsNeedingEscalation = new List<(Discovery.Core.Entities.Ticket ticket, double percentUsed)>();

            await Parallel.ForEachAsync(openTickets, parallelOptions, async (ticket, innerCt) =>
            {
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
                        ), innerCt);
                    }
                }
                else
                {
                    var (_, percentUsed, _) = await slaService.GetSlaStatusAsync(ticket.Id);

                    if (percentUsed >= 80 && percentUsed < 85)
                    {
                        if (await ShouldLogWarningAsync(redis, ticket.Id))
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
                                ), innerCt);
                            }

                            logger.LogWarning("SLA Warning: Ticket {TicketId} - {Percent}% used",
                                ticket.Id, percentUsed.ToString("F2"));
                        }
                    }
                    else
                    {
                        // Limpar warning cooldown se voltou a ficar saudável
                        await redis.DeleteAsync($"sla:warning:{ticket.Id:N}");
                    }

                    // Coletar tickets para escalonamento (processado em batch depois)
                    if (percentUsed >= 80)
                    {
                        lock (ticketsNeedingEscalation)
                        {
                            ticketsNeedingEscalation.Add((ticket, percentUsed));
                        }
                    }
                }
            });

            // Processar escalonamentos
            if (ticketsNeedingEscalation.Count > 0)
            {
                await ProcessEscalationBatchAsync(
                    ticketsNeedingEscalation, slaService, escalationRuleRepo,
                    notificationService, ticketRepo, logger, ct);
            }
        }
        finally
        {
            // Liberar lock
            await redis.DeleteAsync(LockKey);
        }
    }

    private static async Task<bool> ShouldLogWarningAsync(IRedisService redis, Guid ticketId)
    {
        var key = $"sla:warning:{ticketId:N}";
        var acquired = await redis.SetIfNotExistsAsync(key, DateTime.UtcNow.Ticks.ToString(), WarningCooldownMinutes * 60);
        return acquired;
    }

    private static async Task ProcessEscalationBatchAsync(
        List<(Discovery.Core.Entities.Ticket ticket, double percentUsed)> items,
        ISlaService slaService,
        ITicketEscalationRuleRepository escalationRuleRepo,
        INotificationService notificationService,
        ITicketRepository ticketRepo,
        ILogger logger,
        CancellationToken ct)
    {
        // Coletar tickets que precisam de bump de prioridade
        var ticketsToBump = new List<(Guid ticketId, Discovery.Core.Enums.TicketPriority newPriority)>();

        foreach (var (ticket, percentUsed) in items)
        {
            if (!ticket.WorkflowProfileId.HasValue) continue;

            var rules = await escalationRuleRepo.GetByWorkflowProfileIdAsync(ticket.WorkflowProfileId.Value);
            var now = DateTime.UtcNow;

            foreach (var rule in rules.Where(r => r.IsActive))
            {
                bool shouldFire = false;

                if (rule.TriggerAtSlaPercent > 0 && percentUsed >= rule.TriggerAtSlaPercent)
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
                    var newPriority = (Discovery.Core.Enums.TicketPriority)((int)ticket.Priority + 1);
                    ticketsToBump.Add((ticket.Id, newPriority));
                    logger.LogInformation("Escalation: queued priority bump for ticket {TicketId} to {Priority}", ticket.Id, newPriority);
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

        // Batch update de prioridades
        foreach (var (ticketId, newPriority) in ticketsToBump)
        {
            var ticket = await ticketRepo.GetByIdAsync(ticketId);
            if (ticket is not null)
            {
                ticket.Priority = newPriority;
                await ticketRepo.UpdateAsync(ticket);
            }
        }
    }
}
