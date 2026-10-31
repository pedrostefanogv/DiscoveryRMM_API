using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Sessão de chat de suporte. Pode estar vinculada a um chamado ou ser
/// AVULSA (TicketId nulo), aberta direto pela lista/detalhe do agent.
///
/// O histórico vive no SERVIDOR (tabelas ticket_chat_sessions/messages) e é
/// consultável apenas pelo console web. O agent mantém somente o estado em
/// memória enquanto a janela do chat está aberta.
/// </summary>
public class TicketChatSession
{
    public Guid Id { get; set; }

    /// <summary>Chamado vinculado; nulo em conversas AVULSAS (sem chamado).</summary>
    public Guid? TicketId { get; set; }

    /// <summary>Agent (máquina) do chamado no momento da abertura.</summary>
    public Guid? AgentId { get; set; }

    public Guid ClientId { get; set; }

    public Guid? SiteId { get; set; }

    public TicketChatSessionStatus Status { get; set; } = TicketChatSessionStatus.Pending;

    /// <summary>Atendente que iniciou a sessão (único autorizado a iniciar).</summary>
    public Guid? StartedByUserId { get; set; }

    public string? StartedByName { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Prazo do convite; passado o prazo o job marca a sessão como Expired.</summary>
    public DateTime? InviteExpiresAt { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public DateTime? DeclinedAt { get; set; }

    /// <summary>user_denied | timeout | busy | offline.</summary>
    public string? DeclinedReason { get; set; }

    public DateTime? EndedAt { get; set; }

    public TicketChatEndedBy? EndedBy { get; set; }

    /// <summary>support_closed | user_closed | inactivity | ticket_closed.</summary>
    public string? EndReason { get; set; }

    public DateTime? LastMessageAt { get; set; }

    public int MessageCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// O histórico desta conversa pode ser exposto a ESTE atendente?
    ///
    /// Conversa vinculada a chamado segue a regra de acesso do chamado (o
    /// histórico é do atendimento, não do operador). Conversa AVULSA é privada
    /// de quem a iniciou: sem isso o painel da máquina devolvia a última sessão
    /// da máquina e outro atendente (ou outro turno) via a conversa anterior de
    /// um usuário final que não era o dele.
    ///
    /// Sessões antigas sem StartedByUserId ficam invisíveis (fail-closed).
    /// </summary>
    public bool IsVisibleToSupportUser(Guid? supportUserId)
        => TicketId is not null
            || (supportUserId is not null
                && StartedByUserId is not null
                && StartedByUserId == supportUserId);

    public Ticket? Ticket { get; set; }

    public Agent? Agent { get; set; }

    public ICollection<TicketChatMessage> Messages { get; set; } = new List<TicketChatMessage>();
}
