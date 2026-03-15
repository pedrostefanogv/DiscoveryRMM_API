using Meduza.Core.Entities;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Meduza.Core.DTOs;

namespace Meduza.Infrastructure.Repositories;

public class AgentLabelRepository : IAgentLabelRepository
{
    private readonly MeduzaDbContext _db;

    public AgentLabelRepository(MeduzaDbContext db) => _db = db;

    public async Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId)
    {
        return await _db.AgentLabels
            .AsNoTracking()
            .Where(label => label.AgentId == agentId)
            .OrderBy(label => label.Label)
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
}
