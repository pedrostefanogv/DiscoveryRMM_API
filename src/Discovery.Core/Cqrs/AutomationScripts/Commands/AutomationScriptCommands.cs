using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.AutomationScripts.Commands;

public sealed record CreateAutomationScriptCommand(
    string Name,
    string Summary,
    AutomationScriptType ScriptType,
    string Version,
    string Content,
    bool IsActive,
    Guid? ClientId = null,
    string? ChangedBy = null,
    string? IpAddress = null,
    string? CorrelationId = null
) : ICommand<Result<AutomationScriptDetailDto>>;

public sealed record UpdateAutomationScriptCommand(
    Guid Id,
    string Name,
    string Summary,
    AutomationScriptType ScriptType,
    string Version,
    string Content,
    bool IsActive,
    string? Reason = null,
    string? ChangedBy = null,
    string? IpAddress = null,
    string? CorrelationId = null
) : ICommand<Result<AutomationScriptDetailDto>>;

public sealed record DeleteAutomationScriptCommand(
    Guid Id,
    string? Reason = null,
    string? ChangedBy = null,
    string? IpAddress = null,
    string? CorrelationId = null
) : ICommand<Result<VoidResult>>;
