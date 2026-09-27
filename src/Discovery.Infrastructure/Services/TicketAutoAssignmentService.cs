using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;

namespace Discovery.Infrastructure.Services;

public class TicketAutoAssignmentService(
    ITicketAssignmentService assignmentService,
    IAiAssignmentQueueRepository queue,
    DiscoveryDbContext db) : ITicketAutoAssignmentService
{
    public async Task<AutoAssignmentResolution> ResolveAsync(
        Guid? departmentId, Guid? explicitAssignee, CancellationToken ct = default)
    {
        // Responsável explícito sempre vence (ex.: regra de alerta com atendente fixo).
        if (explicitAssignee.HasValue || departmentId is null)
            return new AutoAssignmentResolution(explicitAssignee, false, null);

        var strategy = await assignmentService.GetStrategyAsync(departmentId.Value, ct);
        if (strategy is null || strategy == (int)TicketAssignmentStrategy.None)
            return new AutoAssignmentResolution(null, false, strategy);

        if (strategy == (int)TicketAssignmentStrategy.AiTriage)
            return new AutoAssignmentResolution(null, true, strategy);

        var assignee = await assignmentService.ResolveFallbackAsync(departmentId.Value, strategy.Value, ct);
        return new AutoAssignmentResolution(assignee, false, strategy);
    }

    public async Task<bool> ApplyAfterCreateAsync(Ticket ticket, CancellationToken ct = default)
    {
        if (ticket.AssignedToUserId.HasValue || ticket.DepartmentId is null) return false;

        var resolution = await ResolveAsync(ticket.DepartmentId, null, ct);

        if (resolution.QueueAiTriage)
        {
            await queue.EnqueueAsync(ticket.Id, ticket.DepartmentId.Value, "ticket_created_auto", ct);
            return true;
        }

        if (!resolution.AssignedToUserId.HasValue) return false;

        ticket.AssignedToUserId = resolution.AssignedToUserId;
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
