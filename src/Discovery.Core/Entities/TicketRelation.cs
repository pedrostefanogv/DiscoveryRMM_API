namespace Discovery.Core.Entities;

/// <summary>
/// Relação entre dois tickets (duplicate, blocks, relates-to, parent-child).
/// </summary>
public class TicketRelation
{
    public Guid Id { get; set; }
    public Guid SourceTicketId { get; set; }
    public Guid TargetTicketId { get; set; }
    public int RelationTypeValue { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public Ticket SourceTicket { get; set; } = null!;
    public Ticket TargetTicket { get; set; } = null!;
}
