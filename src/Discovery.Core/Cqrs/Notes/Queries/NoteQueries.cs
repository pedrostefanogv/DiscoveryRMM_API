using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Notes.Commands;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Notes.Queries;

public sealed record ListNotesPageQuery(
    Guid? ClientId = null,
    Guid? SiteId = null,
    Guid? AgentId = null,
    string? Cursor = null,
    int Limit = 50
) : IQuery<Result<CursorPageDto<NoteDto>>>;

public sealed record GetNoteByIdQuery(Guid Id) : IQuery<Result<NoteDto>>;
