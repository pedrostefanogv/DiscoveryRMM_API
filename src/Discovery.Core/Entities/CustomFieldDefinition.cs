using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class CustomFieldDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CustomFieldScopeType ScopeType { get; set; } = CustomFieldScopeType.Agent;
    public CustomFieldDataType DataType { get; set; } = CustomFieldDataType.Text;
    public bool IsRequired { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSecret { get; set; }
    public string? OptionsJson { get; set; }
    public string? ValidationRegex { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public bool AllowRuntimeRead { get; set; }
    public bool AllowAgentWrite { get; set; }
    public CustomFieldRuntimeAccessMode RuntimeAccessMode { get; set; } = CustomFieldRuntimeAccessMode.Disabled;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
