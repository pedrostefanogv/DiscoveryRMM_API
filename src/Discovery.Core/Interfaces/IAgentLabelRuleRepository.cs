using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentLabelRuleRepository
{
    Task<IReadOnlyList<AgentLabelRule>> GetAllAsync(bool includeDisabled = true);
    Task<IReadOnlyList<AgentLabelRule>> GetEnabledAsync();
    Task<AgentLabelRule?> GetByIdAsync(Guid id);
    Task<AgentLabelRule> CreateAsync(AgentLabelRule rule);
    Task UpdateAsync(AgentLabelRule rule);

    /// <summary>
    /// Cria/atualiza varias regras em UMA gravacao (por Id). Usado pelo import para
    /// nao pagar um SaveChanges por regra.
    /// </summary>
    Task UpsertRangeAsync(IReadOnlyCollection<AgentLabelRule> rules, CancellationToken ct = default);

    Task DeleteAsync(Guid id);

    /// <summary>Grava um snapshot da regra (auditoria/versionamento de configuracao).</summary>
    Task AddVersionAsync(AgentLabelRule rule, string? changedBy, CancellationToken ct = default);

    /// <summary>Grava snapshots de varias regras em uma unica gravacao (import).</summary>
    Task AddVersionsAsync(IReadOnlyCollection<AgentLabelRule> rules, CancellationToken ct = default);

    /// <summary>Versoes de uma regra, mais recente primeiro.</summary>
    Task<IReadOnlyList<AgentLabelRuleVersion>> GetVersionsAsync(Guid ruleId, int limit, CancellationToken ct = default);
}
