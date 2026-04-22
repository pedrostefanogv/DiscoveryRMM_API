namespace Discovery.Core.Entities;

/// <summary>
/// Registro de sessão de suporte remoto MeshCentral iniciada no contexto de um ticket.
/// </summary>
public class TicketRemoteSession
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }

    /// <summary>Agent alvo da sessão (opcional quando a sessão é por nome/mesh node).</summary>
    public Guid? AgentId { get; set; }

    /// <summary>MeshCentral node ID do dispositivo alvo.</summary>
    public string? MeshNodeId { get; set; }

    /// <summary>URL de embedding gerada para esta sessão.</summary>
    public string? SessionUrl { get; set; }

    /// <summary>Usuário que iniciou a sessão (username ou userId).</summary>
    public string? StartedBy { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    /// <summary>Duração total em segundos (preenchido ao encerrar).</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Nota livre sobre o que foi feito na sessão.</summary>
    public string? Note { get; set; }

    public Ticket Ticket { get; set; } = null!;
}
