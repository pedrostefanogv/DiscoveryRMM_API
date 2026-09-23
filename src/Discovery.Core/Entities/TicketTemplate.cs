using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>Template de chamado: pré-preenche título, descrição, prioridade, categoria e campos.</summary>
public class TicketTemplate
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? DepartmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TicketPriority? Priority { get; set; }
    public string? Category { get; set; }
    public string CustomFieldDefaultsJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
