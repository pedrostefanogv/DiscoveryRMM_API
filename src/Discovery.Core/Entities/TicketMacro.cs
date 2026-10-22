namespace Discovery.Core.Entities;

/// <summary>Resposta rápida (macro) reutilizável em comentários de chamados.</summary>
public class TicketMacro
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? DepartmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Runbook executável (M242): JSON com a lista de <see cref="RunbookStep"/>.
    /// null = macro sem runbook (resposta rápida simples).
    /// </summary>
    public string? RunbookStepsJson { get; set; }

    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
