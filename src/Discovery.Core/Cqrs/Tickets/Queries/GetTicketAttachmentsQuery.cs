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
