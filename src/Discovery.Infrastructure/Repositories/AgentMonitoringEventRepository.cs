using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AgentMonitoringEventRepository : IAgentMonitoringEventRepository
{
    private readonly DiscoveryDbContext _db;

    public AgentMonitoringEventRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AgentMonitoringEvent?> GetByIdAsync(Guid id)
    {
        return await _db.AgentMonitoringEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(monitoringEvent => monitoringEvent.Id == id);
    }

    public async Task<IReadOnlyList<AgentMonitoringEvent>> GetByFiltersPageAsync(
        Guid? agentId,
        Guid? clientId,
        Guid? siteId,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        var query = _db.AgentMonitoringEvents.AsNoTracking().AsQueryable();

        if (agentId.HasValue)
            query = query.Where(e => e.AgentId == agentId.Value);

        if (clientId.HasValue)
            query = query.Where(e => e.ClientId == clientId.Value);

        if (siteId.HasValue)
            query = query.Where(e => e.SiteId == siteId.Value);

        if (CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorCreatedAtUtc, out var cursorId))
        {
            query = CursorPaginationHelper.ApplyCreatedAtCursor(
                query, cursorCreatedAtUtc, cursorId,
                e => e.CreatedAt, e => e.Id);
        }

        return await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(safeLimit + 1)
            .ToListAsync(ct);
    }

    public async Task<AgentMonitoringEvent> CreateAsync(AgentMonitoringEvent monitoringEvent)
    {
        monitoringEvent.Id = monitoringEvent.Id == Guid.Empty ? IdGenerator.NewId() : monitoringEvent.Id;
        monitoringEvent.CreatedAt = DateTime.UtcNow;
        monitoringEvent.OccurredAt = monitoringEvent.OccurredAt == default ? DateTime.UtcNow : monitoringEvent.OccurredAt;
        _db.AgentMonitoringEvents.Add(monitoringEvent);
        await _db.SaveChangesAsync();
        return monitoringEvent;
    }
}