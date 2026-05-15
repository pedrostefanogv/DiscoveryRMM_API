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

    public async Task<IReadOnlyList<AgentLabelRuleAgentResponse>> GetAgentsByRuleIdAsync(Guid ruleId)
    {
        return await (from match in _db.AgentLabelRuleMatches.AsNoTracking()
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
            .ToListAsync();
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
