using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AgentAlertRepository : IAgentAlertRepository
{
    private readonly DiscoveryDbContext _db;

    public AgentAlertRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AgentAlertDefinition?> GetByIdAsync(Guid id)
        => await _db.AgentAlertDefinitions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);

    public async Task<IReadOnlyList<AgentAlertDefinition>> GetByFiltersAsync(
        AlertDefinitionStatus? status = null,
        AlertScopeType? scopeType = null,
        Guid? scopeClientId = null,
        Guid? scopeSiteId = null,
        Guid? scopeAgentId = null,
        Guid? ticketId = null,
        int limit = 100,
        int offset = 0)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var safeOffset = Math.Max(0, offset);

        var query = _db.AgentAlertDefinitions.AsNoTracking().AsQueryable();

        if (status.HasValue)
            query = query.Where(a => a.Status == status.Value);

        if (scopeType.HasValue)
            query = query.Where(a => a.ScopeType == scopeType.Value);

        if (scopeClientId.HasValue)
            query = query.Where(a => a.ScopeClientId == scopeClientId.Value);

        if (scopeSiteId.HasValue)
            query = query.Where(a => a.ScopeSiteId == scopeSiteId.Value);

        if (scopeAgentId.HasValue)
            query = query.Where(a => a.ScopeAgentId == scopeAgentId.Value);

        if (ticketId.HasValue)
            query = query.Where(a => a.TicketId == ticketId.Value);

        return await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<AgentAlertDefinition>> GetPendingScheduledAsync(DateTime utcNow)
        => await _db.AgentAlertDefinitions
            .AsNoTracking()
            .Where(a => a.Status == AlertDefinitionStatus.Scheduled && a.ScheduledAt <= utcNow)
            .ToListAsync();

    public async Task<IReadOnlyList<AgentAlertDefinition>> GetExpiredAsync(DateTime utcNow)
        => await _db.AgentAlertDefinitions
            .AsNoTracking()
            .Where(a =>
                (a.Status == AlertDefinitionStatus.Draft || a.Status == AlertDefinitionStatus.Scheduled) &&
                a.ExpiresAt.HasValue && a.ExpiresAt.Value <= utcNow)
            .ToListAsync();

    public async Task<AgentAlertDefinition> CreateAsync(AgentAlertDefinition alert)
    {
        alert.Id = Guid.NewGuid();
        alert.CreatedAt = DateTime.UtcNow;
        alert.UpdatedAt = DateTime.UtcNow;
        _db.AgentAlertDefinitions.Add(alert);
        await _db.SaveChangesAsync();
        return alert;
    }

    public async Task UpdateAsync(AgentAlertDefinition alert)
    {
        alert.UpdatedAt = DateTime.UtcNow;
        _db.AgentAlertDefinitions.Update(alert);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateStatusAsync(Guid id, AlertDefinitionStatus status, DateTime? dispatchedAt = null, int? dispatchedCount = null)
    {
        var alert = await _db.AgentAlertDefinitions.FirstOrDefaultAsync(a => a.Id == id);
        if (alert is null) return;

        alert.Status = status;
        alert.UpdatedAt = DateTime.UtcNow;

        if (dispatchedAt.HasValue)
            alert.DispatchedAt = dispatchedAt;

        if (dispatchedCount.HasValue)
            alert.DispatchedCount = dispatchedCount.Value;

        await _db.SaveChangesAsync();
    }
}
