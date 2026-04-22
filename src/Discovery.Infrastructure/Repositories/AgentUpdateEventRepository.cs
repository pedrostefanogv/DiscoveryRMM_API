using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AgentUpdateEventRepository(DiscoveryDbContext db) : IAgentUpdateEventRepository
{
    public async Task<AgentUpdateEvent> CreateAsync(AgentUpdateEvent updateEvent, CancellationToken cancellationToken = default)
    {
        updateEvent.Id = IdGenerator.NewId();
        updateEvent.CreatedAt = DateTime.UtcNow;

        db.AgentUpdateEvents.Add(updateEvent);
        await db.SaveChangesAsync(cancellationToken);
        return updateEvent;
    }

    public async Task<IReadOnlyList<AgentUpdateEvent>> GetByAgentIdAsync(Guid agentId, int limit = 100, CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);

        return await db.AgentUpdateEvents
            .AsNoTracking()
            .Where(item => item.AgentId == agentId)
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentUpdateRolloutAgentSnapshotDto>> GetRolloutSnapshotsAsync(Guid? clientId = null, Guid? siteId = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);

        var query =
            from agent in db.Agents.AsNoTracking()
            join site in db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in db.Clients.AsNoTracking() on site.ClientId equals client.Id
            where (!clientId.HasValue || client.Id == clientId.Value)
                && (!siteId.HasValue || site.Id == siteId.Value)
            select new AgentUpdateRolloutAgentSnapshotDto(
                agent.Id,
                agent.Hostname,
                agent.DisplayName,
                agent.Status,
                agent.AgentVersion,
                client.Id,
                client.Name,
                site.Id,
                site.Name,
                db.AgentUpdateEvents
                    .Where(item => item.AgentId == agent.Id)
                    .OrderByDescending(item => item.OccurredAtUtc)
                    .ThenByDescending(item => item.CreatedAt)
                    .Select(item => item.AgentReleaseId)
                    .FirstOrDefault(),
                db.AgentUpdateEvents
                    .Where(item => item.AgentId == agent.Id)
                    .OrderByDescending(item => item.OccurredAtUtc)
                    .ThenByDescending(item => item.CreatedAt)
                    .Select(item => (Discovery.Core.Enums.AgentUpdateEventType?)item.EventType)
                    .FirstOrDefault(),
                db.AgentUpdateEvents
                    .Where(item => item.AgentId == agent.Id)
                    .OrderByDescending(item => item.OccurredAtUtc)
                    .ThenByDescending(item => item.CreatedAt)
                    .Select(item => item.TargetVersion)
                    .FirstOrDefault(),
                db.AgentUpdateEvents
                    .Where(item => item.AgentId == agent.Id)
                    .OrderByDescending(item => item.OccurredAtUtc)
                    .ThenByDescending(item => item.CreatedAt)
                    .Select(item => item.Message)
                    .FirstOrDefault(),
                db.AgentUpdateEvents
                    .Where(item => item.AgentId == agent.Id)
                    .OrderByDescending(item => item.OccurredAtUtc)
                    .ThenByDescending(item => item.CreatedAt)
                    .Select(item => (DateTime?)item.OccurredAtUtc)
                    .FirstOrDefault());

        return await query
            .OrderByDescending(item => item.LastEventAtUtc)
            .ThenBy(item => item.ClientName)
            .ThenBy(item => item.SiteName)
            .ThenBy(item => item.Hostname)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);
    }
}
