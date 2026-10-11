namespace Discovery.Core.DTOs;

/// <summary>
/// Tipos de evento do envelope do chat de suporte.
/// </summary>
public static class TicketChatEventTypes
{
    /// <summary>Servidor -> agent: sessão aberta (usuário aceitou o convite).</summary>
    public const string SessionOpened = "SessionOpened";

    /// <summary>Bidirecional: nova mensagem.</summary>
    public const string Message = "Message";

    /// <summary>Servidor -> agent: sessão encerrada (suporte/sistema).</summary>
    public const string SessionEnded = "SessionEnded";

    /// <summary>Agent -> servidor: resposta ao convite (aceito/recusado).</summary>
    public const string InviteResponse = "InviteResponse";
}

/// <summary>
/// Envelope do chat de suporte trafegado no NATS
/// (tenant.{c}.site.{s}.agent.{a}.supportchat.{server|agent}).
///
/// Contrato: JSON camelCase, `timestampUtc` em ISO-8601 UTC (mesma regra do
/// contrato de realtime). `clientMessageId` é a chave de idempotência ponta a
/// ponta (dedupe no broker via DuplicateWindow e índice único na persistência).
/// </summary>
public class TicketChatEnvelope
{
    public string EventType { get; set; } = string.Empty;

    public Guid SessionId { get; set; }

    public Guid TicketId { get; set; }

    public Guid AgentId { get; set; }

    /// <summary>Sequência atribuída pelo servidor; ausente para eventos de controle.</summary>
    public long? Sequence { get; set; }

    /// <summary>support | enduser | system.</summary>
    public string? AuthorKind { get; set; }

    public string? AuthorName { get; set; }

    public string? Body { get; set; }

    public Guid[]? AttachmentIds { get; set; }

    public Guid ClientMessageId { get; set; }

    /// <summary>support_closed | user_closed | inactivity | ticket_closed.</summary>
    public string? EndReason { get; set; }

    /// <summary>Preenchido em InviteResponse.</summary>
    public bool? Accepted { get; set; }

    /// <summary>user_denied | timeout | busy | offline.</summary>
    public string? Reason { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
