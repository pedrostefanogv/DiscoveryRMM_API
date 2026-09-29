using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface ILabelService
{
    // Labels
    Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Labels de varios agentes em uma unica consulta.</summary>
    Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDistinctLabelsAsync(int limit, AgentLabelSourceType? sourceType, CancellationToken ct = default);
    Task<AgentLabel?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<AgentLabel> AddAsync(AgentLabel label, CancellationToken ct = default);

    /// <summary>Adiciona a label e limpa qualquer supressao previa com o mesmo nome.</summary>
    Task<AgentLabel> AddWithSuppressionClearAsync(AgentLabel label, CancellationToken ct = default);
    /// <summary>Retorna false quando a label nao existe (permite responder 404).</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Remove a label e, se ela for automatica, registra a supressao para que o
    /// reconcile nao a recrie. Retorna false quando a label nao existe.
    /// </summary>
    Task<bool> DeleteWithSuppressionAsync(Guid id, string? suppressedBy, CancellationToken ct = default);

    // Rules
    Task<IReadOnlyList<AgentLabelRule>> GetRulesAsync(bool includeDisabled = true, CancellationToken ct = default);
    Task<AgentLabelRule?> GetRuleByIdAsync(Guid id, CancellationToken ct = default);
    Task<AgentLabelRule> CreateRuleAsync(AgentLabelRule rule, CancellationToken ct = default);
    Task UpdateRuleAsync(AgentLabelRule rule, CancellationToken ct = default);
    Task DeleteRuleAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Cria/atualiza varias regras em uma unica gravacao e invalida o cache UMA vez.
    /// </summary>
    Task ImportRulesAsync(IReadOnlyList<AgentLabelRule> rules, CancellationToken ct = default);

    /// <summary>Versoes de uma regra (auditoria de configuracao), mais recente primeiro.</summary>
    Task<IReadOnlyList<LabelRuleVersionDto>> GetRuleVersionsAsync(Guid ruleId, int limit, CancellationToken ct = default);

    // Labels protegidas (modo Remover)
    Task<IReadOnlyList<AgentLabelProtectedLabelDto>> GetProtectedLabelsAsync(CancellationToken ct = default);
    Task<AgentLabelProtectedLabelDto> AddProtectedLabelAsync(string label, string? createdBy, CancellationToken ct = default);
    Task<bool> RemoveProtectedLabelAsync(Guid id, CancellationToken ct = default);

    // Agents matched by a rule
    Task<(int Total, IReadOnlyList<AgentLabelRuleAgentResponse> Agents)> GetAgentsByRuleIdPagedAsync(
        Guid ruleId,
        int page,
        int pageSize,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAgentIdsByLabelPagedAsync(string label, Guid? afterAgentId, int limit, CancellationToken ct = default);

    /// <summary>Supressoes de labels de um agente (visivel ao usuario).</summary>
    Task<IReadOnlyList<AgentLabelSuppressionDto>> GetSuppressionsByAgentIdAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Libera uma supressao e devolve o agente afetado (null quando nao existe).</summary>
    Task<Guid?> ReleaseSuppressionAsync(Guid suppressionId, CancellationToken ct = default);

    /// <summary>Historico de aplicacao/remocao de labels do agente (auditoria).</summary>
    Task<IReadOnlyList<AgentLabelChangeLogDto>> GetChangeLogAsync(Guid agentId, int limit, CancellationToken ct = default);
    Task<int> CountAgentsByLabelAsync(string label, CancellationToken ct = default);
    Task<IReadOnlyList<AgentLabelUsageDto>> GetLabelUsageAsync(int limit, CancellationToken ct = default);
}
