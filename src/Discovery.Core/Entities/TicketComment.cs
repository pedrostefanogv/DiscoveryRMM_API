namespace Discovery.Core.Entities;

public class TicketComment
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Soft delete: o comentário excluído continua LEGÍVEL no histórico (o
    /// conteúdo permanece) mas fica visivelmente marcado como excluído.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Quem excluiu: usuário do console ("nome") ou agente ("agent:{id}").</summary>
    public string? DeletedBy { get; set; }

    public bool IsDeleted => DeletedAt.HasValue;
}
