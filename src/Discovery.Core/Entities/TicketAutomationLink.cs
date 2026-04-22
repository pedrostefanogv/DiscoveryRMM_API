using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Vínculo entre um ticket e uma tarefa de automação, suportando fluxo de aprovação.
/// </summary>
public class TicketAutomationLink
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid AutomationTaskDefinitionId { get; set; }

    public TicketAutomationLinkStatus Status { get; set; } = TicketAutomationLinkStatus.Pending;

    public string? RequestedBy { get; set; }
    public string? ReviewedBy { get; set; }
    public string? Note { get; set; }

    public DateTime RequestedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }

    // Navegação
    public Ticket? Ticket { get; set; }
    public AutomationTaskDefinition? AutomationTask { get; set; }
}
