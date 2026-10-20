using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Anexo de uma mensagem do chat de suporte, já resolvido para exibição
/// (join ticket_chat_message_attachments x attachments).
/// </summary>
public sealed record TicketChatAttachmentRef(
    Guid MessageId,
    Guid AttachmentId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string? UploadedBy,
    DateTime CreatedAt);

/// <summary>
/// Persistência do chat de suporte vinculado ao chamado.
/// </summary>
public interface ITicketChatRepository
{
    Task<TicketChatSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Sessão pendente/ativa do chamado (no máximo uma).</summary>
    Task<TicketChatSession?> GetCurrentForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default);

    /// <summary>Sessão pendente/ativa da máquina (no máximo uma).</summary>
    Task<TicketChatSession?> GetCurrentForAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sessão de MAIOR StartedAt do chamado, com QUALQUER status (inclui a
    /// última Declined/Expired/Ended). Usada pelo console para enxergar o
    /// desfecho: GetCurrentForTicketAsync filtra Pending/Active e devolvia null
    /// logo após a recusa, congelando o painel em "Aguardando confirmação".
    ///
    /// Distinta de <see cref="GetCurrentForTicketAsync"/> de propósito: a
    /// "atual" continua servindo às decisões de "ocupado"/reaproveitar sessão.
    /// </summary>
    Task<TicketChatSession?> GetLatestForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default);

    /// <summary>Sessão de MAIOR StartedAt da máquina, com QUALQUER status.</summary>
    Task<TicketChatSession?> GetLatestForAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    Task AddSessionAsync(TicketChatSession session, CancellationToken cancellationToken = default);

    Task UpdateSessionAsync(TicketChatSession session, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TicketChatSession>> GetHistoryAsync(Guid ticketId, int skip, int take, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TicketChatMessage>> GetMessagesAsync(Guid sessionId, long afterSequence, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Insere a mensagem com `sequence_number` alocado em transação e vincula os
    /// anexos. Idempotente por `(session_id, client_message_id)`: quando a
    /// mensagem já existe, devolve a existente sem duplicar (e sem recontar).
    /// </summary>
    Task<(TicketChatMessage Message, bool Created)> AppendMessageAsync(
        Guid sessionId,
        TicketChatMessage message,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TicketChatAttachmentRef>> GetAttachmentsForMessagesAsync(
        IReadOnlyList<Guid> messageIds,
        CancellationToken cancellationToken = default);

    /// <summary>Convites pendentes vencidos (job de manutenção).</summary>
    Task<IReadOnlyList<TicketChatSession>> GetExpiredPendingAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>Sessões ativas sem atividade desde `cutoffUtc` (job de manutenção).</summary>
    Task<IReadOnlyList<TicketChatSession>> GetInactiveActiveAsync(DateTime cutoffUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>Sessões ativas de chamados já encerrados (job de manutenção).</summary>
    Task<IReadOnlyList<TicketChatSession>> GetActiveForClosedTicketsAsync(int limit, CancellationToken cancellationToken = default);
}
