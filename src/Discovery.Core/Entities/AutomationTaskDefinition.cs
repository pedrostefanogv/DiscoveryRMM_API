using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AutomationTaskDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AutomationTaskActionType ActionType { get; set; } = AutomationTaskActionType.RunScript;

    // Package actions
    public AppInstallationType? InstallationType { get; set; }
    public string? PackageId { get; set; }

    // Script action
    public Guid? ScriptId { get; set; }

    // Custom action
    public string? CommandPayload { get; set; }

    // Scope resolution
    public AppApprovalScopeType ScopeType { get; set; } = AppApprovalScopeType.Global;
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }

    // Target filters
    public string? IncludeTagsJson { get; set; }
    public string? ExcludeTagsJson { get; set; }

    // Trigger options
    public bool TriggerImmediate { get; set; }
    public bool TriggerRecurring { get; set; }
    public bool TriggerOnUserLogin { get; set; }
    public bool TriggerOnAgentCheckIn { get; set; }
    public string? ScheduleCron { get; set; }

    // Governance
    public bool RequiresApproval { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? DeletedAt { get; set; }

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
