namespace Discovery.Core.Entities;

public class TicketComment
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
    public DateTime CreatedAt { get; set; }
}
