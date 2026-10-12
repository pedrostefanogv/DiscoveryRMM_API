using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Tickets.Queries;

/// <summary>
/// Query to retrieve comments for a ticket with cursor pagination.
/// </summary>
public sealed record GetTicketCommentsQuery(
    Guid TicketId,
    string? Cursor,
    int Limit,
    /// <summary>false = notas internas são ocultadas (usuário sem permissão de edição).</summary>
    bool IncludeInternal = false
) : IQuery<Result<CursorPageDto<TicketCommentDto>>>;

/// <summary>
/// Um comentário específico do chamado (com anexos). Usado para validar que o
/// comentário pertence ao chamado antes de anexar/excluir (anti-IDOR) e para
/// devolver o comentário atualizado.
/// </summary>
public sealed record GetTicketCommentQuery(Guid TicketId, Guid CommentId) : IQuery<Result<TicketCommentDto>>;

/// <summary>
/// Anexo de um comentário (nível conversa: pequeno, com miniatura).
/// </summary>
public sealed record TicketCommentAttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string? UploadedBy,
    DateTime CreatedAt);

/// <summary>
/// Lightweight DTO for ticket comment listings.
/// Comentário excluído continua legível: <see cref="Content"/> permanece e
/// <see cref="DeletedAt"/>/<see cref="DeletedBy"/> marcam a exclusão.
/// </summary>
public sealed record TicketCommentDto(
    Guid Id,
    string Author,
    string Content,
    bool IsInternal,
    DateTime CreatedAt,
    DateTime? DeletedAt = null,
    string? DeletedBy = null,
    IReadOnlyList<TicketCommentAttachmentDto>? Attachments = null
);
