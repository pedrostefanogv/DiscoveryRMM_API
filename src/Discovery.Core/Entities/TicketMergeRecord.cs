namespace Discovery.Core.Entities;

/// <summary>
/// Registro de merge de tickets. Armazena histórico de quando um ticket
/// foi incorporado (merged) em outro.
/// </summary>
public class TicketMergeRecord
{
    public Guid Id { get; set; }
    public Guid SourceTicketId { get; set; }
    public Guid TargetTicketId { get; set; }
    public string? MergedBy { get; set; }
    public string? Reason { get; set; }
    public DateTime MergedAt { get; set; }

    public Ticket SourceTicket { get; set; } = null!;
    public Ticket TargetTicket { get; set; } = null!;
}
