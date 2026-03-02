namespace Meduza.Core.Entities;

public class Ticket
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid WorkflowStateId { get; set; }
    public Enums.TicketPriority Priority { get; set; } = Enums.TicketPriority.Medium;
    public string? AssignedTo { get; set; }
    public string? Category { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}
