namespace Discovery.Core.Entities;

/// <summary>
/// Seguidor de um ticket. Ao adicionar um watcher, ele passa a receber
/// notificações de comentários públicos, mudanças de estado e fechamento.
/// </summary>
public class TicketWatcher
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }

    /// <summary>UserId da plataforma que está seguindo o ticket.</summary>
    public Guid UserId { get; set; }

    /// <summary>Quem adicionou este watcher ("system", username, userId).</summary>
    public string? AddedBy { get; set; }

    public DateTime AddedAt { get; set; }
}
