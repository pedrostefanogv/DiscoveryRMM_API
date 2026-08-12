using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public sealed class LabelService : ILabelService
{
    private readonly IAgentLabelRepository _labels;
    private readonly IAgentLabelRuleRepository _rules;
    public LabelService(IAgentLabelRepository labels, IAgentLabelRuleRepository rules) { _labels = labels; _rules = rules; }

    public Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId, CancellationToken ct = default) => _labels.GetByAgentIdAsync(agentId);
    public Task<IReadOnlyList<string>> GetDistinctLabelsAsync(CancellationToken ct = default) => _labels.GetDistinctLabelsAsync();
    public Task<AgentLabel?> GetByIdAsync(Guid id, CancellationToken ct = default) => _labels.GetByIdAsync(id);
    public Task<AgentLabel> AddAsync(AgentLabel label, CancellationToken ct = default) => _labels.AddAsync(label);
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _labels.DeleteAsync(id);
    public Task<IReadOnlyList<AgentLabelRule>> GetRulesAsync(bool includeDisabled = true, CancellationToken ct = default) => _rules.GetAllAsync(includeDisabled);
    public Task<AgentLabelRule?> GetRuleByIdAsync(Guid id, CancellationToken ct = default) => _rules.GetByIdAsync(id);
    public Task<AgentLabelRule> CreateRuleAsync(AgentLabelRule rule, CancellationToken ct = default) => _rules.CreateAsync(rule);
    public Task UpdateRuleAsync(AgentLabelRule rule, CancellationToken ct = default) => _rules.UpdateAsync(rule);
    public Task DeleteRuleAsync(Guid id, CancellationToken ct = default) => _rules.DeleteAsync(id);
    public Task<IReadOnlyList<AgentLabelRuleAgentResponse>> GetAgentsByRuleIdAsync(Guid ruleId, CancellationToken ct = default) => _labels.GetAgentsByRuleIdAsync(ruleId);
}
