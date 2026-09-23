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

    public async Task<IReadOnlyList<Guid>> GetAgentIdsByLabelPagedAsync(
        string label,
        Guid? afterAgentId,
        int limit,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);

        return await _db.AgentLabels
            .AsNoTracking()
            .Where(item => item.Label == label)
            .Where(item => !afterAgentId.HasValue || item.AgentId.CompareTo(afterAgentId.Value) > 0)
            .OrderBy(item => item.AgentId)
            .Select(item => item.AgentId)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public Task<int> CountAgentsByLabelAsync(string label, CancellationToken ct = default)
        => _db.AgentLabels
            .AsNoTracking()
            .Where(item => item.Label == label)
            .Select(item => item.AgentId)
            .Distinct()
            .CountAsync(ct);

    public async Task<IReadOnlyList<AgentLabelUsageDto>> GetLabelUsageAsync(int limit, CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);

        return await _db.AgentLabels
            .AsNoTracking()
            .GroupBy(item => item.Label)
            .Select(group => new AgentLabelUsageDto
            {
                Label = group.Key,
                AgentCount = group.Select(item => item.AgentId).Distinct().Count()
            })
            .OrderByDescending(item => item.AgentCount)
            .ThenBy(item => item.Label)
            .Take(safeLimit)
            .ToListAsync(ct);
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

    public async Task SuppressAutomaticLabelAsync(Guid agentId, string label, string? suppressedBy, CancellationToken ct = default)
    {
        // Idempotente: se ja existe supressao para (agente, label), apenas mantem.
        var existing = await _db.AgentLabelSuppressions
            .FirstOrDefaultAsync(s => s.AgentId == agentId && s.Label == label, ct);

        if (existing is not null)
            return;

        _db.AgentLabelSuppressions.Add(new AgentLabelSuppression
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            Label = label,
            SuppressedAt = DateTime.UtcNow,
            SuppressedBy = suppressedBy
        });

        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearSuppressionAsync(Guid agentId, string label, CancellationToken ct = default)
    {
        // Evita acumular supressao orfa quando o usuario readiciona a label na mao.
        // Carrega e remove via ChangeTracker em vez de ExecuteDeleteAsync: a operacao e
        // de cardinalidade minima (no maximo 1 linha) e ExecuteDelete nao e suportado
        // por todos os providers (ex.: InMemory nos testes, SQLite).
        var existing = await _db.AgentLabelSuppressions
            .Where(s => s.AgentId == agentId && s.Label == label)
            .ToListAsync(ct);

        if (existing.Count == 0)
            return;

        _db.AgentLabelSuppressions.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AgentLabelSuppressionDto>> GetSuppressionsByAgentIdAsync(Guid agentId, CancellationToken ct = default)
    {
        // Junta com as regras para dar contexto ao usuario ("por que esta suprimida").
        var suppressions = await _db.AgentLabelSuppressions
            .AsNoTracking()
            .Where(s => s.AgentId == agentId)
            .OrderBy(s => s.Label)
            .ToListAsync(ct);

        if (suppressions.Count == 0)
            return [];

        var labels = suppressions.Select(s => s.Label).Distinct().ToList();
        var ruleNameByLabel = await _db.AgentLabelRules
            .AsNoTracking()
            .Where(rule => labels.Contains(rule.Label))
            .GroupBy(rule => rule.Label)
            .Select(group => new { Label = group.Key, Name = group.OrderBy(rule => rule.Name).First().Name })
            .ToDictionaryAsync(item => item.Label, item => item.Name, ct);

        return suppressions
            .Select(s => new AgentLabelSuppressionDto
            {
                Id = s.Id,
                AgentId = s.AgentId,
                Label = s.Label,
                SuppressedAt = s.SuppressedAt,
                SuppressedBy = s.SuppressedBy,
                RuleName = ruleNameByLabel.TryGetValue(s.Label, out var name) ? name : null
            })
            .ToList();
    }

    public async Task<bool> ReleaseSuppressionAsync(Guid suppressionId, CancellationToken ct = default)
    {
        var suppression = await _db.AgentLabelSuppressions
            .FirstOrDefaultAsync(s => s.Id == suppressionId, ct);

        if (suppression is null)
            return false;

        _db.AgentLabelSuppressions.Remove(suppression);
        await _db.SaveChangesAsync(ct);
        return true;
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
