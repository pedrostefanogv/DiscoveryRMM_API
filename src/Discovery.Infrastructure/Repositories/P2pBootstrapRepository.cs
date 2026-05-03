using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class P2pBootstrapRepository : IP2pBootstrapRepository
{
    private readonly DiscoveryDbContext _db;

    public P2pBootstrapRepository(DiscoveryDbContext db)
    {
        _db = db;
    }

    public async Task UpsertAsync(AgentP2pBootstrap bootstrap)
    {
        var existing = await _db.AgentP2pBootstraps
            .FirstOrDefaultAsync(b => b.AgentId == bootstrap.AgentId);

        if (existing is not null)
        {
            existing.ClientId = bootstrap.ClientId;
            existing.PeerId = bootstrap.PeerId;
            existing.AddrsJson = bootstrap.AddrsJson;
            existing.Port = bootstrap.Port;
            existing.LastHeartbeatAt = bootstrap.LastHeartbeatAt;
        }
        else
        {
            _db.AgentP2pBootstraps.Add(bootstrap);
        }

        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AgentP2pBootstrap>> GetRandomPeersAsync(
        Guid clientId,
        Guid excludeAgentId,
        int count,
        DateTime onlineCutoff)
    {
        // JOIN com agents para verificar LastSeenAt (critério de online decidido pelo usuário)
        var peers = await _db.AgentP2pBootstraps
            .AsNoTracking()
            .Where(b => b.ClientId == clientId && b.AgentId != excludeAgentId)
            .Join(
                _db.Agents.Where(a => a.LastSeenAt != null && a.LastSeenAt >= onlineCutoff),
                b => b.AgentId,
                a => a.Id,
                (b, _) => b)
            .OrderBy(_ => EF.Functions.Random())
            .Take(count)
            .ToListAsync();

        return peers;
    }

    public async Task<IReadOnlyList<AgentP2pBootstrap>> GetSitePeersAsync(
        Guid siteId,
        DateTime onlineCutoff,
        int maxPeers)
    {
        var peers = await _db.AgentP2pBootstraps
            .AsNoTracking()
            .Join(
                _db.Agents.Where(a => a.SiteId == siteId
                    && a.LastSeenAt != null
                    && a.LastSeenAt >= onlineCutoff),
                b => b.AgentId,
                a => a.Id,
                (b, _) => b)
            .OrderByDescending(b => b.LastHeartbeatAt)
            .Take(maxPeers)
            .ToListAsync();

        return peers;
    }
}
