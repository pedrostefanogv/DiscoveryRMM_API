using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Contagem de agentes por site (total e online) em uma query agregada.
/// O filtro global de soft delete do agente já exclui os de lixeira.
/// </summary>
public class SiteAgentCountService : ISiteAgentCountService
{
    private readonly DiscoveryDbContext _db;

    public SiteAgentCountService(DiscoveryDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<Guid, SiteAgentCount>> GetCountsBySiteIdsAsync(
        IReadOnlyCollection<Guid> siteIds,
        CancellationToken ct = default)
    {
        if (siteIds.Count == 0)
            return new Dictionary<Guid, SiteAgentCount>();

        var ids = siteIds.Distinct().ToArray();
        var rows = await _db.Agents
            .AsNoTracking()
            .Where(agent => ids.Contains(agent.SiteId))
            .GroupBy(agent => agent.SiteId)
            .Select(group => new
            {
                SiteId = group.Key,
                Total = group.Count(),
                Online = group.Count(agent => agent.Status == AgentStatus.Online)
            })
            .ToListAsync(ct);

        return rows.ToDictionary(
            row => row.SiteId,
            row => new SiteAgentCount(row.Total, row.Online));
    }
}
