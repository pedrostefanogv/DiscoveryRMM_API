using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public class AutomationTaskSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AutomationTaskActionType ActionType { get; set; }
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public bool IsActive { get; set; }
    public bool RequiresApproval { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class AutomationTaskDetailDto : AutomationTaskSummaryDto
{
    public AppInstallationType? InstallationType { get; set; }
    public string? PackageId { get; set; }
    public Guid? ScriptId { get; set; }
    public string? CommandPayload { get; set; }
    public IReadOnlyList<string> IncludeTags { get; set; } = [];
    public IReadOnlyList<string> ExcludeTags { get; set; } = [];
    public bool TriggerImmediate { get; set; }
    public bool TriggerRecurring { get; set; }
    public bool TriggerOnUserLogin { get; set; }
    public bool TriggerOnAgentCheckIn { get; set; }
    public string? ScheduleCron { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AutomationTaskPageDto
{
    public IReadOnlyList<AutomationTaskSummaryDto> Items { get; set; } = [];
    public int Count { get; set; }
    public int Total { get; set; }
    public int Limit { get; set; }
    public int Offset { get; set; }
}

public class AutomationTaskAuditDto
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public string? ChangedBy { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime ChangedAt { get; set; }
}

public class AutomationTaskTargetPreviewItemDto
{
    public Guid AgentId { get; set; }
    public Guid SiteId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public AgentStatus Status { get; set; }
    public IReadOnlyList<string> AgentTags { get; set; } = [];
}

public class AutomationTaskTargetPreviewPageDto
{
    public Guid TaskId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public AppApprovalScopeType ScopeType { get; set; }
    public IReadOnlyList<string> IncludeTags { get; set; } = [];
    public IReadOnlyList<string> ExcludeTags { get; set; } = [];
    public IReadOnlyList<AutomationTaskTargetPreviewItemDto> Items { get; set; } = [];
    public int Count { get; set; }
    public int Total { get; set; }
    public int Limit { get; set; }
    public int Offset { get; set; }
}

public class CreateAutomationTaskRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AutomationTaskActionType ActionType { get; set; }
    public AppInstallationType? InstallationType { get; set; }
    public string? PackageId { get; set; }
    public Guid? ScriptId { get; set; }
    public string? CommandPayload { get; set; }
    public AppApprovalScopeType ScopeType { get; set; } = AppApprovalScopeType.Global;
    public Guid? ScopeId { get; set; }
    public IReadOnlyList<string> IncludeTags { get; set; } = [];
    public IReadOnlyList<string> ExcludeTags { get; set; } = [];
    public bool TriggerImmediate { get; set; }
    public bool TriggerRecurring { get; set; }
    public bool TriggerOnUserLogin { get; set; }
    public bool TriggerOnAgentCheckIn { get; set; }
    public string? ScheduleCron { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UpdateAutomationTaskRequest : CreateAutomationTaskRequest
{
    public string? Reason { get; set; }
}
