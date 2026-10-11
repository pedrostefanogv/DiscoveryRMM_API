namespace Discovery.Core.Entities;

/// <summary>
/// Vínculo entre uma mensagem do chat de suporte e um anexo do object storage.
/// </summary>
public class TicketChatMessageAttachment
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public Guid AttachmentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TicketChatMessage Message { get; set; } = null!;
}
