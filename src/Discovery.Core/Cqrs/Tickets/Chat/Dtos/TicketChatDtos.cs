namespace Discovery.Core.Cqrs.Tickets.Chat.Dtos;

/// <summary>Anexo (object storage) vinculado a uma mensagem do chat de suporte.</summary>
public sealed record TicketChatAttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string? UploadedBy,
    DateTime CreatedAt);

/// <summary>
/// Mensagem do chat de suporte.
///
/// `ClientMessageId` é a chave de idempotência gerada pelo EMISSOR: o agent usa
/// para casar a bolha otimista (ainda sem sequência) com a mensagem canônica
/// devolvida pelo servidor, sem depender de heurística por corpo/horário.
/// </summary>
public sealed record TicketChatMessageDto(
    Guid Id,
    long SequenceNumber,
    Guid ClientMessageId,
    string AuthorKind,
    Guid? AuthorUserId,
    string? AuthorName,
    string Body,
    DateTime CreatedAt,
    IReadOnlyList<TicketChatAttachmentDto> Attachments);

/// <summary>
/// Estado de uma sessão de chat de suporte. `Messages` só é populado nas
/// consultas que pedem o histórico completo.
/// </summary>
public sealed record TicketChatSessionDto(
    Guid Id,
    Guid? TicketId,
    Guid? AgentId,
    string Status,
    Guid? StartedByUserId,
    string? StartedByName,
    DateTime StartedAt,
    DateTime? InviteExpiresAt,
    DateTime? AcceptedAt,
    DateTime? DeclinedAt,
    string? DeclinedReason,
    DateTime? EndedAt,
    string? EndedBy,
    string? EndReason,
    DateTime? LastMessageAt,
    int MessageCount,
    IReadOnlyList<TicketChatMessageDto>? Messages);

/// <summary>
/// Estado do chat de um chamado/agent. `HasSession=false` representa "não há
/// sessão pendente/ativa" sem transformar a ausência em erro HTTP.
/// </summary>
public sealed record TicketChatStateDto(bool HasSession, TicketChatSessionDto? Session);
