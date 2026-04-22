using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class CustomFieldValue
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public CustomFieldScopeType ScopeType { get; set; }
    public Guid? EntityId { get; set; }
    public string EntityKey { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "null";
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
