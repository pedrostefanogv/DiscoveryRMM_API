using System.Collections.Concurrent;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Discovery.Api.Services;

/// <summary>
/// Serviço de background que monitora SLAs de tickets e marca violações.
/// Executa a cada 5 minutos por padrão.
/// </summary>
public class SlaMonitoringBackgroundService : BackgroundService
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan WarningCooldown = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SlaMonitoringBackgroundService> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _warningLoggedAtUtc = new();

    public SlaMonitoringBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<SlaMonitoringBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SLA Monitoring Background Service starting...");

        // Aguardar um pouco para que a aplicação inicialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var nextDelay = ActiveInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                nextDelay = await CheckSlaBreachesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                nextDelay = ActiveInterval;
                _logger.LogError(ex, "Error occurred during SLA breach check");
            }

            await Task.Delay(nextDelay, stoppingToken);
        }

        _logger.LogInformation("SLA Monitoring Background Service stopped");
    }

    private async Task<TimeSpan> CheckSlaBreachesAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        
        var ticketRepo = scope.ServiceProvider.GetRequiredService<ITicketRepository>();
        var slaService = scope.ServiceProvider.GetRequiredService<ISlaService>();
        var activityLogService = scope.ServiceProvider.GetRequiredService<IActivityLogService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var escalationRuleRepo = scope.ServiceProvider.GetRequiredService<ITicketEscalationRuleRepository>();

        try
        {
            // Obter todos os tickets abertos com SLA configurado
            var openTickets = await ticketRepo.GetOpenTicketsWithSlaAsync();

            if (openTickets == null || !openTickets.Any())
            {
                _logger.LogDebug("No open tickets with SLA to check");
                return IdleInterval;
            }

            _logger.LogInformation("Checking SLA for {Count} open tickets", openTickets.Count);

            foreach (var ticket in openTickets)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var breached = await slaService.CheckAndLogSlaBreachAsync(ticket.Id);

                if (breached)
                {
                    _logger.LogWarning("SLA Breached: Ticket {TicketId} - {Title}", ticket.Id, ticket.Title);

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
                        ), cancellationToken);
                    }
                }
                else
                {
                    // Verificar se está próximo de violar (80%)
                    var (_, percentUsed, _) = await slaService.GetSlaStatusAsync(ticket.Id);
                    
                    if (percentUsed >= 80 && percentUsed < 85)
                    {
                        if (ShouldLogWarning(ticket.Id))
                        {
                            await activityLogService.LogActivityAsync(
                                ticket.Id,
                                TicketActivityType.SlaWarning,
                                null,
                                percentUsed.ToString("F2"),
                                "80",
                                "SLA warning: 20% time remaining"
                            );

                            if (ticket.AssignedToUserId.HasValue)
                            {
                                await notificationService.PublishAsync(new NotificationPublishRequest(
                                    EventType: "ticket.sla_warning",
                                    Topic: "tickets",
                                    Title: "Aviso de SLA",
                                    Message: $"O ticket '{ticket.Title}' utilizou {percentUsed:F0}% do tempo de SLA. Apenas {(100 - percentUsed):F0}% restante.",
                                    Severity: NotificationSeverity.Warning,
                                    Payload: new { ticketId = ticket.Id, percentUsed },
                                    RecipientUserId: ticket.AssignedToUserId
                                ), cancellationToken);
                            }

                            _logger.LogWarning("SLA Warning: Ticket {TicketId} - Only {Percent}% time remaining", 
                                ticket.Id, (100 - percentUsed).ToString("F2"));
                        }
                    }
                    else
                    {
                        _warningLoggedAtUtc.TryRemove(ticket.Id, out _);
                    }

                    // Verificar regras de escalação ativas
                    await ProcessEscalationRulesAsync(
                        ticket, percentUsed, slaService, escalationRuleRepo, notificationService, ticketRepo, cancellationToken);
                }
            }

            return ActiveInterval;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in CheckSlaBreachesAsync");
            throw;
        }
    }

    private async Task ProcessEscalationRulesAsync(
        Discovery.Core.Entities.Ticket ticket,
        double slaPercentUsed,
        ISlaService slaService,
        ITicketEscalationRuleRepository escalationRuleRepo,
        INotificationService notificationService,
        ITicketRepository ticketRepo,
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
                _logger.LogInformation("Escalation: bumped priority of ticket {TicketId} to {Priority}", ticket.Id, ticket.Priority);
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

    private bool ShouldLogWarning(Guid ticketId)
    {
        var now = DateTime.UtcNow;
        if (_warningLoggedAtUtc.TryGetValue(ticketId, out var lastLoggedAtUtc) && now - lastLoggedAtUtc < WarningCooldown)
            return false;

        _warningLoggedAtUtc[ticketId] = now;
        return true;
    }
}
