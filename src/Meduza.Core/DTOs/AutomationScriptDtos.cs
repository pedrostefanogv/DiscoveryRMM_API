using Meduza.Core.Enums;

namespace Meduza.Core.DTOs;

public class AutomationScriptSummaryDto
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public AutomationScriptType ScriptType { get; set; }
    public string Version { get; set; } = string.Empty;
    public string ExecutionFrequency { get; set; } = string.Empty;
    public IReadOnlyList<string> TriggerModes { get; set; } = [];
    public bool IsActive { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AutomationScriptDetailDto : AutomationScriptSummaryDto
{
    public string Content { get; set; } = string.Empty;
    public string ContentHashSha256 { get; set; } = string.Empty;
    public string? ParametersSchemaJson { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AutomationScriptConsumeDto
{
    public Guid ScriptId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public AutomationScriptType ScriptType { get; set; }
    public string ExecutionFrequency { get; set; } = string.Empty;
    public IReadOnlyList<string> TriggerModes { get; set; } = [];
    public string Summary { get; set; } = string.Empty;
    public DateTime LastUpdatedAt { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ContentHashSha256 { get; set; } = string.Empty;
    public string? ParametersSchemaJson { get; set; }
    public string? MetadataJson { get; set; }
}

public class AutomationScriptPageDto
{
    public IReadOnlyList<AutomationScriptSummaryDto> Items { get; set; } = [];
    public int Count { get; set; }
    public int Total { get; set; }
    public int Limit { get; set; }
    public int Offset { get; set; }
}

public class AutomationScriptAuditDto
{
    public Guid Id { get; set; }
    public Guid ScriptId { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public string? ChangedBy { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime ChangedAt { get; set; }
}

public class CreateAutomationScriptRequest
{
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public AutomationScriptType ScriptType { get; set; } = AutomationScriptType.PowerShell;
    public string Version { get; set; } = "1.0.0";
    public string ExecutionFrequency { get; set; } = "manual";
    public IReadOnlyList<string> TriggerModes { get; set; } = [];
    public string Content { get; set; } = string.Empty;
    public string? ParametersSchemaJson { get; set; }
    public string? MetadataJson { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UpdateAutomationScriptRequest
{
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public AutomationScriptType ScriptType { get; set; } = AutomationScriptType.PowerShell;
    public string Version { get; set; } = "1.0.0";
    public string ExecutionFrequency { get; set; } = "manual";
    public IReadOnlyList<string> TriggerModes { get; set; } = [];
    public string Content { get; set; } = string.Empty;
    public string? ParametersSchemaJson { get; set; }
    public string? MetadataJson { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Reason { get; set; }
}
