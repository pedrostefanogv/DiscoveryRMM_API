using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Tickets.Queries;

/// <summary>
/// Query para listar anexos de um ticket com paginação por cursor.
/// </summary>
public sealed record GetTicketAttachmentsQuery(
    Guid TicketId,
    string? Cursor = null,
    int Limit = 50
) : IQuery<Result<CursorPageDto<TicketAttachmentDto>>>;

/// <summary>
/// Lista CONSOLIDADA de anexos do chamado (repositório + conversa), paginada
/// por cursor. Sem este endpoint a aba Anexos só enxergava os anexos de
/// conversa dos primeiros 200 comentários — anexos antigos desapareciam.
/// </summary>
public sealed record GetTicketAttachmentsAllQuery(
    Guid TicketId,
    string? Cursor = null,
    int Limit = 50
) : IQuery<Result<CursorPageDto<TicketAttachmentEntryDto>>>;

/// <summary>
/// Anexo do chamado com a ORIGEM explícita: "repository" (EntityType "Ticket")
/// ou "comment" (EntityType "TicketComment"). Nos de comentário vêm também o
/// autor e se o comentário foi excluído (soft delete).
/// </summary>
public sealed record TicketAttachmentEntryDto(
    Guid Id,
    string FileName,
    string? Description,
    string ContentType,
    long SizeBytes,
    string? UploadedBy,
    DateTime CreatedAt,
    string Origin,
    Guid? CommentId = null,
    string? CommentAuthor = null,
    bool CommentDeleted = false);

/// <summary>
/// DTO leve para anexo de ticket (sem dados binários).
/// </summary>
public sealed record TicketAttachmentDto(
    Guid Id,
    string FileName,
    string? Description,
    string ContentType,
    long SizeBytes,
    string? UploadedBy,
    DateTime CreatedAt
);
