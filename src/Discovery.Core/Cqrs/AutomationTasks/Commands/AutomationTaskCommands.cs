using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.AutomationTasks.Commands;

public sealed record CreateAutomationTaskCommand(
    string Name,
    string? Description,
    AutomationTaskActionType ActionType,
    AppInstallationType? InstallationType,
    string? PackageId,
    Guid? ScriptId,
    string? CommandPayload,
    AppApprovalScopeType ScopeType,
    Guid? ScopeId,
    IReadOnlyList<string> IncludeTags,
    IReadOnlyList<string> ExcludeTags,
    bool TriggerImmediate,
    bool TriggerRecurring,
    bool TriggerOnUserLogin,
    bool TriggerOnAgentCheckIn,
    string? ScheduleCron,
    bool RequiresApproval,
    bool IsActive,
    bool AllowDefer = true,
    IReadOnlyList<string>? CloseProcesses = null,
    int PromptTimeoutSeconds = 60,
    AutomationNotificationMode? NotificationMode = null,
    AutomationToastTiming? ToastTiming = null,
    string? ChangedBy = null,
    string? IpAddress = null,
    string? CorrelationId = null
) : ICommand<Result<AutomationTaskDetailDto>>;

public sealed record UpdateAutomationTaskCommand(
    Guid Id,
    string? Name,
    string? Description,
    AutomationTaskActionType? ActionType,
    AppInstallationType? InstallationType,
    string? PackageId,
    Guid? ScriptId,
    string? CommandPayload,
    AppApprovalScopeType? ScopeType,
    Guid? ScopeId,
    IReadOnlyList<string>? IncludeTags,
    IReadOnlyList<string>? ExcludeTags,
    bool? TriggerImmediate,
    bool? TriggerRecurring,
    bool? TriggerOnUserLogin,
    bool? TriggerOnAgentCheckIn,
    string? ScheduleCron,
    bool? RequiresApproval,
    bool? IsActive,
    bool? AllowDefer = null,
    IReadOnlyList<string>? CloseProcesses = null,
    int? PromptTimeoutSeconds = null,
    AutomationNotificationMode? NotificationMode = null,
    AutomationToastTiming? ToastTiming = null,
    string? Reason = null,
    string? ChangedBy = null,
    string? IpAddress = null,
    string? CorrelationId = null
) : ICommand<Result<AutomationTaskDetailDto>>;

public sealed record DeleteAutomationTaskCommand(
    Guid Id,
    string? Reason = null,
    string? ChangedBy = null,
    string? IpAddress = null,
    string? CorrelationId = null
) : ICommand<Result<VoidResult>>;

public sealed record RestoreAutomationTaskCommand(
    Guid Id,
    string? Reason = null,
    string? ChangedBy = null,
    string? IpAddress = null,
    string? CorrelationId = null
) : ICommand<Result<AutomationTaskDetailDto>>;
