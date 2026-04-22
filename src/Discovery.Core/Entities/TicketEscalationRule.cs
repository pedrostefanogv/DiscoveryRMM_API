namespace Discovery.Core.Entities;

/// <summary>
/// Regra de escalaçao automática vinculada a um WorkflowProfile.
/// Quando o SLA atinge o limiar configurado, ações são disparadas automaticamente.
/// </summary>
public class TicketEscalationRule
{
    public Guid Id { get; set; }

    /// <summary>Perfil de workflow ao qual esta regra se aplica.</summary>
    public Guid WorkflowProfileId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Aciona quando o percentual de SLA usado atingir este valor (0 = desativado).
    /// Exemplo: 80 = acionar quando 80% do tempo de SLA já foi consumido.
    /// </summary>
    public int TriggerAtSlaPercent { get; set; } = 0;

    /// <summary>
    /// Aciona quando restar este número de horas para o SLA vencer (0 = desativado).
    /// Exemplo: 2 = acionar quando faltarem 2 horas.
    /// </summary>
    public int TriggerAtHoursBefore { get; set; } = 0;

    /// <summary>Se preenchido, reatribui o ticket ao usuário especificado.</summary>
    public Guid? ReassignToUserId { get; set; }

    /// <summary>Se preenchido, move o ticket para o departamento especificado.</summary>
    public Guid? ReassignToDepartmentId { get; set; }

    /// <summary>Se true, eleva a prioridade do ticket um nível acima do atual.</summary>
    public bool BumpPriority { get; set; } = false;

    /// <summary>Se true, notifica o assignee atual via notificação da plataforma.</summary>
    public bool NotifyAssignee { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
