using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Discovery.Infrastructure.Repositories;

public class AgentRepository : IAgentRepository
{
    private readonly DiscoveryDbContext _db;

    public AgentRepository(DiscoveryDbContext db) => _db = db;

    public async Task<Agent?> GetByIdAsync(Guid id)
    {
        return await _db.Agents
            .AsNoTracking()
            .SingleOrDefaultAsync(agent => agent.Id == id);
    }

    public async Task<IEnumerable<Agent>> GetAllAsync()
    {
        return await _db.Agents
            .AsNoTracking()
            .OrderBy(agent => agent.Hostname)
            .ToListAsync();
    }

    public async Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId)
    {
        return await _db.Agents
            .AsNoTracking()
            .Where(agent => agent.SiteId == siteId)
            .OrderBy(agent => agent.Hostname)
            .ToListAsync();
    }

    public async Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId)
    {
        return await (
            from agent in _db.Agents.AsNoTracking()
            join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            where site.ClientId == clientId
            orderby agent.Hostname
            select agent)
            .ToListAsync();
    }

    public async Task<Agent> CreateAsync(Agent agent)
    {
        agent.Id = IdGenerator.NewId();
        agent.CreatedAt = DateTime.UtcNow;
        agent.UpdatedAt = DateTime.UtcNow;

        _db.Agents.Add(agent);
        await _db.SaveChangesAsync();
        return agent;
    }

    public async Task UpdateAsync(Agent agent)
    {
        var existingAgent = await _db.Agents.SingleOrDefaultAsync(existing => existing.Id == agent.Id);
        if (existingAgent is null)
            return;

        existingAgent.SiteId = agent.SiteId;
        existingAgent.Hostname = agent.Hostname;
        existingAgent.DisplayName = agent.DisplayName;
        existingAgent.MeshCentralNodeId = agent.MeshCentralNodeId;
        existingAgent.Status = agent.Status;
        existingAgent.OperatingSystem = agent.OperatingSystem;
        existingAgent.OsVersion = agent.OsVersion;
        existingAgent.AgentVersion = agent.AgentVersion;
        existingAgent.LastIpAddress = agent.LastIpAddress;
        existingAgent.MacAddress = agent.MacAddress;
        existingAgent.LastSeenAt = agent.LastSeenAt;
        existingAgent.ZeroTouchPending = agent.ZeroTouchPending;
        existingAgent.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task ApproveZeroTouchAsync(Guid agentId)
    {
        var now = DateTime.UtcNow;
        await _db.Agents
            .Where(agent => agent.Id == agentId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(agent => agent.ZeroTouchPending, _ => false)
                .SetProperty(agent => agent.UpdatedAt, _ => now));
    }

    public async Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default)
    {
        return await _db.Agents
            .AsNoTracking()
            .Where(agent => agent.Status == AgentStatus.Online)
            .OrderBy(agent => agent.Hostname)
            .ToListAsync(ct);
    }

    public async Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress)
    {
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var now = DateTime.UtcNow;

                await _db.Agents
                    .Where(agent => agent.Id == id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(agent => agent.Status, _ => status)
                        .SetProperty(agent => agent.LastIpAddress, _ => ipAddress)
                        .SetProperty(agent => agent.LastSeenAt, _ => now)
                        .SetProperty(agent => agent.UpdatedAt, _ => now));
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                _db.ChangeTracker.Clear();
                await Task.Delay(150);
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is TimeoutException)
            return true;

        if (ex is NpgsqlException npgsqlEx)
            return npgsqlEx.IsTransient || npgsqlEx.InnerException is TimeoutException;

        return false;
    }

    public async Task DeleteAsync(Guid id)
    {
        await _db.Agents
            .Where(agent => agent.Id == id)
            .ExecuteDeleteAsync();
    }
}
