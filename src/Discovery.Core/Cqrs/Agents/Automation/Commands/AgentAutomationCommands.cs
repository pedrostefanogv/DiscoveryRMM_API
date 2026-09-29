using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Automation.Commands;

public sealed record RunAutomationTaskCommand(Guid AgentId, Guid TaskId, string? CorrelationId = null) : ICommand<Result<AutomationExecutionDto>>;
public sealed record RunAutomationScriptCommand(Guid AgentId, Guid ScriptId, string? CorrelationId = null) : ICommand<Result<AutomationExecutionDto>>;
/// <summary>
/// Force sync por agent. As flags selecionam o que o agent deve re-sincronizar:
/// <paramref name="Policies"/>, <paramref name="Inventory"/>, <paramref name="Software"/>
/// e <paramref name="AppStore"/>. Quando todas são nulas (cliente legado sem flags),
/// o handler aplica o default histórico (policies + inventory).
/// </summary>
public sealed record ForceAutomationSyncCommand(
    Guid AgentId,
    string? TaskIds = null,
    bool? Policies = null,
    bool? Inventory = null,
    bool? Software = null,
    bool? AppStore = null,
    string? CorrelationId = null) : ICommand<Result<VoidResult>>;
/// <summary>
/// Cancela uma execução ainda não finalizada. Marca o comando vinculado como
/// <c>Cancelled</c> (estado terminal), o que impede a reentrega automática —
/// útil para abortar um lote que ficou enfileirado para agentes offline.
/// Não interrompe um comando que o agente já começou a executar.
/// </summary>
public sealed record CancelAutomationExecutionCommand(
    Guid AgentId,
    Guid ExecutionId,
    string? CorrelationId = null) : ICommand<Result<AutomationExecutionDto>>;

public sealed record RefreshAgentDataCommand(Guid AgentId, bool ListeningPorts = false, bool OpenConnections = false, bool Software = false, bool Printers = false, bool Hardware = false, bool StartupItems = false, bool ScheduledTasks = false) : ICommand<Result<VoidResult>>;

/// <summary>
/// Execução de automação/operação de software. Os campos extras alimentam a
/// página de operações (detalhe do agente) — inclui as operações de
/// update/uninstall disparadas pelo inventário.
/// </summary>
public sealed record AutomationExecutionDto(
    Guid Id,
    string Status,
    DateTime CreatedAt,
    Guid? CommandId = null,
    Guid AgentId = default,
    Guid? TaskId = null,
    Guid? ScriptId = null,
    string? SourceType = null,
    string? CorrelationId = null,
    DateTime? AcknowledgedAt = null,
    DateTime? ResultReceivedAt = null,
    int? ExitCode = null,
    string? ErrorMessage = null,
    string? RequestMetadataJson = null,
    string? AckMetadataJson = null,
    string? ResultMetadataJson = null,
    // Nomes resolvidos de task/script para a página de operações (null quando a
    // tarefa/script foi excluída ou não é mais resolvível).
    string? TaskName = null,
    string? ScriptName = null
);