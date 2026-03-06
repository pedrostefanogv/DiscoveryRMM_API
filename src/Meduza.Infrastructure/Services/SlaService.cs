using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Meduza.Infrastructure.Services;

public class SlaService : ISlaService
{
    private readonly IWorkflowProfileRepository _workflowProfileRepo;
    private readonly ITicketRepository _ticketRepo;
    private readonly IActivityLogService _activityLogService;
    private readonly ILogger<SlaService> _logger;

    public SlaService(
        IWorkflowProfileRepository workflowProfileRepo,
        ITicketRepository ticketRepo,
        IActivityLogService activityLogService,
        ILogger<SlaService> logger)
    {
        _workflowProfileRepo = workflowProfileRepo;
        _ticketRepo = ticketRepo;
        _activityLogService = activityLogService;
        _logger = logger;
    }

    public async Task<DateTime> CalculateSlaExpiryAsync(Guid workflowProfileId, DateTime createdAt)
    {
        var profile = await _workflowProfileRepo.GetByIdAsync(workflowProfileId);
        if (profile is null)
            throw new InvalidOperationException($"WorkflowProfile {workflowProfileId} not found");

        return createdAt.AddHours(profile.SlaHours);
    }

    public async Task<(int HoursRemaining, double PercentUsed, bool Breached)> GetSlaStatusAsync(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            throw new InvalidOperationException($"Ticket {ticketId} not found");

        if (!ticket.SlaExpiresAt.HasValue)
            return (0, 0, false);

        var now = DateTime.UtcNow;
        var totalSlaTime = (int)(ticket.SlaExpiresAt.Value - ticket.CreatedAt).TotalHours;
        var elapsedTime = (int)(now - ticket.CreatedAt).TotalHours;
        var remainingTime = (int)(ticket.SlaExpiresAt.Value - now).TotalHours;

        var percentUsed = totalSlaTime > 0 ? (elapsedTime / (double)totalSlaTime) * 100 : 0;
        var breached = now > ticket.SlaExpiresAt.Value;

        if (percentUsed > 100)
            percentUsed = 100;

        return (Math.Max(0, remainingTime), percentUsed, breached);
    }

    public async Task<bool> CheckAndLogSlaBreachAsync(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
        {
            _logger.LogWarning("Ticket {TicketId} not found during SLA check", ticketId);
            return false;
        }

        if (ticket.SlaBreached)
            return false; // Já foi marcado como violado

        if (!ticket.SlaExpiresAt.HasValue)
            return false; // Sem SLA

        var now = DateTime.UtcNow;
        if (now > ticket.SlaExpiresAt.Value)
        {
            // Marcar como violado
            ticket.SlaBreached = true;
            await _ticketRepo.UpdateAsync(ticket);

            // Log da atividade
            await _activityLogService.LogActivityAsync(
                ticketId,
                TicketActivityType.SlaBreached,
                null,
                ticket.SlaExpiresAt.Value.ToString("o"),
                now.ToString("o"),
                "SLA violation detected"
            );

            _logger.LogWarning("SLA Breached for ticket {TicketId} (expired at {ExpiresAt})", 
                ticketId, ticket.SlaExpiresAt);
            
            return true;
        }

        return false;
    }
}
