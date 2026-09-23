using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public sealed class LabelService : ILabelService
{
    private readonly IAgentLabelRepository _labels;
    private readonly IAgentLabelRuleRepository _rules;
    private readonly IRedisService _redis;
    private readonly ILogger<LabelService> _logger;

    public LabelService(
        IAgentLabelRepository labels,
        IAgentLabelRuleRepository rules,
        IRedisService redis,
        ILogger<LabelService> logger)
    {
        _labels = labels;
        _rules = rules;
        _redis = redis;
        _logger = logger;
    }

    public Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId, CancellationToken ct = default) => _labels.GetByAgentIdAsync(agentId);

    public Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
        => _labels.GetByAgentIdsAsync(agentIds);
    public Task<IReadOnlyList<string>> GetDistinctLabelsAsync(CancellationToken ct = default) => _labels.GetDistinctLabelsAsync();
    public Task<AgentLabel?> GetByIdAsync(Guid id, CancellationToken ct = default) => _labels.GetByIdAsync(id);
    public Task<AgentLabel> AddAsync(AgentLabel label, CancellationToken ct = default) => _labels.AddAsync(label);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var label = await _labels.GetByIdAsync(id);
        if (label is null)
            return false;

        await _labels.DeleteAsync(id);
        return true;
    }

    public Task<IReadOnlyList<AgentLabelRule>> GetRulesAsync(bool includeDisabled = true, CancellationToken ct = default) => _rules.GetAllAsync(includeDisabled);
    public Task<AgentLabelRule?> GetRuleByIdAsync(Guid id, CancellationToken ct = default) => _rules.GetByIdAsync(id);

    public async Task<AgentLabelRule> CreateRuleAsync(AgentLabelRule rule, CancellationToken ct = default)
    {
        var created = await _rules.CreateAsync(rule);
        await InvalidateEnabledRulesCacheAsync();
        return created;
    }

    public async Task UpdateRuleAsync(AgentLabelRule rule, CancellationToken ct = default)
    {
        await _rules.UpdateAsync(rule);
        await InvalidateEnabledRulesCacheAsync();
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken ct = default)
    {
        await _rules.DeleteAsync(id);
        await InvalidateEnabledRulesCacheAsync();
    }

    public Task<(int Total, IReadOnlyList<AgentLabelRuleAgentResponse> Agents)> GetAgentsByRuleIdPagedAsync(
        Guid ruleId, int page, int pageSize, CancellationToken ct = default)
        => _labels.GetAgentsByRuleIdPagedAsync(ruleId, page, pageSize, ct);

    /// <summary>
    /// Remove o cache de regras habilitadas. Sem isso, criar/editar/desabilitar/excluir
    /// uma regra nao surtia efeito por ate 5 minutos (TTL), inclusive para o job de
    /// reconciliacao e para o reprocessamento manual.
    /// Falhas de cache nunca devem derrubar a escrita — apenas degradam para o TTL.
    /// </summary>
    private async Task InvalidateEnabledRulesCacheAsync()
    {
        try
        {
            await _redis.DeleteAsync(AgentLabelingCacheKeys.EnabledRules);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao invalidar o cache de regras de label; o TTL de {Ttl}s sera usado como fallback.", AgentLabelingCacheKeys.EnabledRulesTtlSeconds);
        }
    }
}
