using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Discovery.Core.DTOs;

namespace Discovery.Infrastructure.Repositories;

public class AgentLabelRepository : IAgentLabelRepository
{
    private readonly DiscoveryDbContext _db;

    public AgentLabelRepository(DiscoveryDbContext db) => _db = db;

    public async Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId)
    {
        return await _db.AgentLabels
            .AsNoTracking()
            .Where(label => label.AgentId == agentId)
            .OrderBy(label => label.SourceType)
            .ThenBy(label => label.Label)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds)
    {
        if (agentIds.Count == 0)
            return [];

        return await _db.AgentLabels
            .AsNoTracking()
            .Where(label => agentIds.Contains(label.AgentId))
            .OrderBy(label => label.AgentId)
            .ThenBy(label => label.Label)
            .ToListAsync();
    }

    public async Task<(int Total, IReadOnlyList<AgentLabelRuleAgentResponse> Agents)> GetAgentsByRuleIdPagedAsync(
        Guid ruleId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var safePage = page < 1 ? 1 : page;
        var safePageSize = Math.Clamp(pageSize, 1, 500);

        // Contagem no banco — antes era feita materializando a lista completa.
        var total = await _db.AgentLabelRuleMatches
            .AsNoTracking()
            .CountAsync(match => match.RuleId == ruleId, ct);

        var agents = await (from match in _db.AgentLabelRuleMatches.AsNoTracking()
                            join agent in _db.Agents.AsNoTracking() on match.AgentId equals agent.Id
                            where match.RuleId == ruleId
                            orderby agent.Hostname
                            select new AgentLabelRuleAgentResponse
                            {
                                AgentId = agent.Id,
                                Hostname = agent.Hostname,
                                DisplayName = agent.DisplayName,
                                Status = agent.Status,
                                MatchedAt = match.MatchedAt,
                                LastEvaluatedAt = match.LastEvaluatedAt
                            })
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        return (total, agents);
    }

    public async Task<IReadOnlyList<string>> GetDistinctLabelsAsync()
    {
        return await _db.AgentLabels
            .AsNoTracking()
            .Select(l => l.Label)
            .Distinct()
            .OrderBy(l => l)
            .ToListAsync();
    }

    public async Task<AgentLabel?> GetByIdAsync(Guid id)
    {
        return await _db.AgentLabels.FindAsync(id);
    }

    public async Task<AgentLabel> AddAsync(AgentLabel label)
    {
        _db.AgentLabels.Add(label);
        await _db.SaveChangesAsync();
        return label;
    }

    public async Task DeleteAsync(Guid id)
    {
        var label = await _db.AgentLabels.FindAsync(id);
        if (label is not null)
        {
            _db.AgentLabels.Remove(label);
            await _db.SaveChangesAsync();
        }
    }
}
