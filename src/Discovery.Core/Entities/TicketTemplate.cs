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
    /// <summary>
    /// Valores padrão (opcionais) para os campos personalizados do departamento.
    /// Não substitui os campos do chamado — apenas pré-preenche.
    /// </summary>
    public string CustomFieldDefaultsJson { get; set; } = "{}";

    /// <summary>
    /// Mini questionário do template (perguntas próprias, não são campos do
    /// chamado). JSON array de <see cref="Discovery.Core.DTOs.TicketTemplateQuestion"/>.
    /// </summary>
    public string QuestionsJson { get; set; } = "[]";

    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Soft delete: preenchido quando o template foi excluído da listagem.
    /// O histórico dos chamados (template_name) não depende do template,
    /// mas a linha permanece para permitir restauração.
    /// </summary>
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
