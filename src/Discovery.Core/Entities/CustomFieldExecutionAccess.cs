namespace Discovery.Core.Entities;

public class CustomFieldExecutionAccess
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? ScriptId { get; set; }
    public bool CanRead { get; set; } = true;
    public bool CanWrite { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
