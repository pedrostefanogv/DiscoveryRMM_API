using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Notes.Commands;

namespace Discovery.Core.Cqrs.Notes.Queries;

public sealed record ListNotesQuery(Guid? ClientId, Guid? SiteId, Guid? AgentId)
    : IQuery<Result<IReadOnlyList<NoteDto>>>;

public sealed record GetNoteByIdQuery(Guid Id) : IQuery<Result<NoteDto>>;
