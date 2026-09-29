using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
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
    Guid? ScriptId = null) : IQuery<Result<IReadOnlyList<AutomationExecutionDto>>>;