using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AutomationScriptDefinition
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public AutomationScriptType ScriptType { get; set; } = AutomationScriptType.PowerShell;
    public string Version { get; set; } = "1.0.0";
    public string ExecutionFrequency { get; set; } = "manual";
    public string TriggerModesJson { get; set; } = "[]";
    public string Content { get; set; } = string.Empty;
    public string? ParametersSchemaJson { get; set; }
    public string? MetadataJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
