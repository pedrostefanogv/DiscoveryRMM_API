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
    // Limiar de aviso preventivo. Acima dele o responsável recebe "Aviso de SLA".
    private const int SlaWarningThresholdPercent = 80;
    // Cooldown por (ticket, regra) para não reescalonar a cada execução (5 min).
    private const int EscalationCooldownMinutes = 360;
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

            var ticketsNeedingEscalation = new List<(Discovery.Core.Entities.Ticket ticket, double percentUsed)>();

            // Processamento SEQUENCIAL: o DbContext é scoped e NÃO é thread-safe.
            // Parallel.ForEachAsync com serviços compartilhados do mesmo scope
            // dispara "A second operation was started on this context instance".
            foreach (var ticket in openTickets)
            {
                ct.ThrowIfCancellationRequested();
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

                    // O cooldown Redis já evita repetição; o gatilho é apenas o
                    // limiar de percentual.
                    if (percentUsed >= SlaWarningThresholdPercent)
                    {
                        if (await ShouldLogWarningAsync(redis, ticket.Id))
                        {
                            await activityLogService.LogActivityAsync(
                                ticket.Id, TicketActivityType.SlaWarning, null,
                                percentUsed.ToString("F2"), SlaWarningThresholdPercent.ToString(),
                                "SLA warning: 20% time remaining");

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
                        // Limpar warning cooldown se voltou a ficar saudável
                        await redis.DeleteAsync($"sla:warning:{ticket.Id:N}");
                    }

                    // Coletar TODOS os tickets do perfil para escalonamento: quem decide
                    // o disparo é cada regra (percentual próprio ou horas antes do
                    // vencimento). Filtrar aqui por 80% fixos impedia que regras com
                    // gatilho abaixo de 80% e regras puramente por antecedência disparassem.
                    if (ticket.WorkflowProfileId.HasValue)
                    {
                        ticketsNeedingEscalation.Add((ticket, percentUsed));
                    }
                }
            }

            // Processar escalonamentos
            if (ticketsNeedingEscalation.Count > 0)
            {
                await ProcessEscalationBatchAsync(
                    ticketsNeedingEscalation, slaService, escalationRuleRepo,
                    notificationService, ticketRepo, activityLogService, redis, logger, ct);
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
        IActivityLogService activityLogService,
        IRedisService redis,
        ILogger logger,
        CancellationToken ct)
    {
        // Coletar tickets que precisam de bump de prioridade
        var ticketsToBump = new List<(Guid ticketId, Discovery.Core.Enums.TicketPriority newPriority)>();
        // No máximo 1 bump e 1 reatribuição por ticket por execução.
        var bumpedTickets = new HashSet<Guid>();
        var reassignedTickets = new HashSet<Guid>();

        // Cache de regras por perfil: evita repetir a consulta para cada ticket do
        // mesmo workflow profile dentro da execução.
        var rulesCache = new Dictionary<Guid, IReadOnlyList<Discovery.Core.Entities.TicketEscalationRule>>();

        foreach (var (ticket, percentUsed) in items)
        {
            if (!ticket.WorkflowProfileId.HasValue) continue;

            var profileId = ticket.WorkflowProfileId.Value;
            if (!rulesCache.TryGetValue(profileId, out var rules))
            {
                rules = (await escalationRuleRepo.GetByWorkflowProfileIdAsync(profileId)).ToList();
                rulesCache[profileId] = rules;
            }

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

                // Dedup: cada regra dispara no máximo 1x por janela de cooldown.
                var cooldownKey = $"sla:escalation:{ticket.Id:N}:{rule.Id:N}";
                var canFire = await redis.SetIfNotExistsAsync(
                    cooldownKey, DateTime.UtcNow.Ticks.ToString(), EscalationCooldownMinutes * 60);
                if (!canFire)
                    continue;

                logger.LogInformation(
                    "Escalation rule {RuleId} fired for ticket {TicketId} at {Percent}%",
                    rule.Id, ticket.Id, percentUsed);

                // Reatribuição: antes a regra apenas armazenava os campos; nada era
                // aplicado ao chamado. No máximo uma por ticket por execução.
                if ((rule.ReassignToUserId.HasValue || rule.ReassignToDepartmentId.HasValue)
                    && reassignedTickets.Add(ticket.Id))
                {
                    var tracked = await ticketRepo.GetByIdAsync(ticket.Id);
                    if (tracked is null)
                    {
                        reassignedTickets.Remove(ticket.Id);
                    }
                    else
                    {
                        var oldAssignee = tracked.AssignedToUserId;
                        var oldDepartment = tracked.DepartmentId;

                        if (rule.ReassignToUserId.HasValue)
                            tracked.AssignedToUserId = rule.ReassignToUserId.Value;
                        if (rule.ReassignToDepartmentId.HasValue)
                            tracked.DepartmentId = rule.ReassignToDepartmentId.Value;

                        await ticketRepo.UpdateAsync(tracked);

                        // Mantém o snapshot em memória coerente para as próximas regras
                        // e para a notificação abaixo.
                        ticket.AssignedToUserId = tracked.AssignedToUserId;

                        if (oldAssignee != tracked.AssignedToUserId)
                            await activityLogService.LogActivityAsync(
                                ticket.Id, TicketActivityType.Assigned, null,
                                oldAssignee?.ToString(), tracked.AssignedToUserId?.ToString(),
                                $"Escalonamento reatribuiu o chamado pela regra '{rule.Name}'");

                        if (oldDepartment != tracked.DepartmentId)
                            await activityLogService.LogActivityAsync(
                                ticket.Id, TicketActivityType.DepartmentChanged, null,
                                oldDepartment?.ToString(), tracked.DepartmentId?.ToString(),
                                $"Escalonamento alterou o departamento pela regra '{rule.Name}'");

                        logger.LogInformation(
                            "Escalation: ticket {TicketId} reassigned by rule {RuleId}",
                            ticket.Id, rule.Id);
                    }
                }

                // Bump priority (no máximo um nível por ticket por execução)
                if (rule.BumpPriority && ticket.Priority < Discovery.Core.Enums.TicketPriority.Critical
                    && bumpedTickets.Add(ticket.Id))
                {
                    var newPriority = (Discovery.Core.Enums.TicketPriority)((int)ticket.Priority + 1);
                    ticketsToBump.Add((ticket.Id, newPriority));
                    logger.LogInformation("Escalation: queued priority bump for ticket {TicketId} to {Priority}", ticket.Id, newPriority);
                }

                // Notify (responsável atual, já considerando eventual reatribuição)
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
