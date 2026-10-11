using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Mensagem de uma sessão de chat de suporte.
/// </summary>
public class TicketChatMessage
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    /// <summary>Alocado pelo servidor dentro de uma transação; único por sessão.</summary>
    public long SequenceNumber { get; set; }

    /// <summary>Chave de idempotência do emissor (GUID). Índice único por sessão.</summary>
    public Guid ClientMessageId { get; set; }

    public TicketChatAuthorKind AuthorKind { get; set; }

    public Guid? AuthorUserId { get; set; }

    public string? AuthorName { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TicketChatSession Session { get; set; } = null!;

    public ICollection<TicketChatMessageAttachment> Attachments { get; set; } = new List<TicketChatMessageAttachment>();
}
