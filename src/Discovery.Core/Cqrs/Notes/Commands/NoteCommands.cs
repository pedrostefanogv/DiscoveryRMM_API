using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Notes.Commands;

public sealed record CreateNoteCommand(
    Guid? ClientId, Guid? SiteId, Guid? AgentId,
    string Content, string? Author, bool IsPinned
) : ICommand<Result<NoteDto>>;

public sealed record UpdateNoteCommand(
    Guid Id, string? Content, string? Author, bool? IsPinned
) : ICommand<Result<NoteDto>>;

public sealed record DeleteNoteCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record NoteDto(
    Guid Id, Guid? ClientId, Guid? SiteId, Guid? AgentId,
    string Content, string? Author, bool IsPinned,
    DateTime CreatedAt, DateTime UpdatedAt
);
