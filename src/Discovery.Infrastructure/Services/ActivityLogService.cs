using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class ActivityLogService : IActivityLogService
{
    private readonly ITicketActivityLogRepository _logRepo;
    private readonly ILogger<ActivityLogService> _logger;

    public ActivityLogService(
        ITicketActivityLogRepository logRepo,
        ILogger<ActivityLogService> logger)
    {
        _logRepo = logRepo;
        _logger = logger;
    }

    public async Task<TicketActivityLog> LogStateChangeAsync(Guid ticketId, Guid? changedByUserId, Guid oldStateId, Guid newStateId)
    {
        var log = new TicketActivityLog
        {
            TicketId = ticketId,
            Type = TicketActivityType.StateChanged,
            ChangedByUserId = changedByUserId,
            OldValue = oldStateId.ToString(),
            NewValue = newStateId.ToString()
        };

        var result = await _logRepo.LogAsync(log);
        _logger.LogInformation("Ticket {TicketId} state changed from {OldState} to {NewState} by {User}",
            ticketId, oldStateId, newStateId, changedByUserId);
        
        return result;
    }

    public async Task<TicketActivityLog> LogAssignmentAsync(Guid ticketId, Guid? changedByUserId, Guid? oldUserId, Guid? newUserId)
    {
        var log = new TicketActivityLog
        {
            TicketId = ticketId,
            Type = TicketActivityType.Assigned,
            ChangedByUserId = changedByUserId,
            OldValue = oldUserId?.ToString(),
            NewValue = newUserId?.ToString()
        };

        var result = await _logRepo.LogAsync(log);
        _logger.LogInformation("Ticket {TicketId} assigned from {OldUser} to {NewUser} by {AssignedBy}",
            ticketId, oldUserId, newUserId, changedByUserId);
        
        return result;
    }

    public async Task<TicketActivityLog> LogActivityAsync(Guid ticketId, TicketActivityType type, Guid? changedByUserId,
        string? oldValue = null, string? newValue = null, string? comment = null)
    {
        var log = new TicketActivityLog
        {
            TicketId = ticketId,
            Type = type,
            ChangedByUserId = changedByUserId,
            OldValue = oldValue,
            NewValue = newValue,
            Comment = comment
        };

        var result = await _logRepo.LogAsync(log);
        _logger.LogInformation("Ticket {TicketId} activity logged: {ActivityType} by {User}",
            ticketId, type, changedByUserId);
        
        return result;
    }

    public async Task<TicketActivityLog> LogPriorityChangeAsync(Guid ticketId, Guid? changedByUserId, string oldPriority, string newPriority)
    {
        return await LogActivityAsync(
            ticketId,
            TicketActivityType.PriorityChanged,
            changedByUserId,
            oldPriority,
            newPriority,
            "Priority updated"
        );
    }

    public async Task<TicketActivityLog> LogDepartmentChangeAsync(Guid ticketId, Guid? changedByUserId, string oldDept, string newDept)
    {
        return await LogActivityAsync(
            ticketId,
            TicketActivityType.DepartmentChanged,
            changedByUserId,
            oldDept,
            newDept,
            "Department reassigned"
        );
    }
}
