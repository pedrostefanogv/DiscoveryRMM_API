using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

public class TicketAssignmentOverrideTracker(DiscoveryDbContext db) : ITicketAssignmentOverrideTracker
{
    public async Task<bool> MarkIfOverriddenAsync(
        Guid ticketId, Guid? newAssigneeUserId, Guid? changedByUserId, CancellationToken ct = default)
    {
        var decision = await db.TicketAssignmentDecisions
            .Where(d => d.TicketId == ticketId
                        && d.Applied
                        && d.OverriddenAt == null
                        && d.ChosenUserId != null)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (decision is null) return false;

        // Trocar para o próprio escolhido pela IA (ou reenviar o mesmo valor) não
        // é sobreposição; remover o responsável é.
        if (newAssigneeUserId.HasValue && newAssigneeUserId.Value == decision.ChosenUserId) return false;

        decision.OverriddenAt = DateTime.UtcNow;
        decision.OverriddenByUserId = changedByUserId;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
