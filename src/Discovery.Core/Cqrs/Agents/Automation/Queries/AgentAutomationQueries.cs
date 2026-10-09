using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.Agents.Automation.Queries;

/// <summary>
/// Histórico de execuções do agent. Os filtros são opcionais e combinam em AND,
/// permitindo a página de operações recortar por status/origem/tarefa/script.
/// </summary>
public sealed record GetAutomationExecutionsQuery(
    Guid AgentId,
    int Limit = 50,
    AutomationExecutionStatus? Status = null,
    AutomationExecutionSourceType? SourceType = null,
    Guid? TaskId = null,
    Guid? ScriptId = null,
    /// <summary>Correlation do lote (identifica todas as execuções de uma operação em massa).</summary>
    string? CorrelationId = null) : IQuery<Result<IReadOnlyList<AutomationExecutionDto>>>;

/// <summary>
/// Prévia das políticas de automação APLICÁVEIS a um agente — o mesmo conjunto
/// entregue no policy-sync, para verificação na aba "Políticas" do detalhe.
/// </summary>
public sealed record GetAgentAutomationPoliciesQuery(Guid AgentId) : IQuery<Result<AgentAutomationPolicyPreviewDto>>;