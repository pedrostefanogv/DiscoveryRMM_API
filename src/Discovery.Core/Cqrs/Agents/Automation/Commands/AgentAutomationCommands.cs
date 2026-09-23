using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Automation.Commands;

public sealed record RunAutomationTaskCommand(Guid AgentId, Guid TaskId) : ICommand<Result<AutomationExecutionDto>>;
public sealed record RunAutomationScriptCommand(Guid AgentId, Guid ScriptId) : ICommand<Result<AutomationExecutionDto>>;
public sealed record ForceAutomationSyncCommand(Guid AgentId, string? TaskIds) : ICommand<Result<VoidResult>>;
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
    string? ResultMetadataJson = null
);