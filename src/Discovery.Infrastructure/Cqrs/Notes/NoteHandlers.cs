using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Notes.Commands;
using Discovery.Core.Cqrs.Notes.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Notes;

public sealed class ListNotesQueryHandler(
    INoteService service
) : IRequestHandler<ListNotesQuery, Result<IReadOnlyList<NoteDto>>>
{
    public async Task<Result<IReadOnlyList<NoteDto>>> Handle(ListNotesQuery q, CancellationToken ct)
    {
        IReadOnlyList<EntityNote> notes;
        if (q.ClientId.HasValue) notes = await service.GetByClientIdAsync(q.ClientId.Value, ct);
        else if (q.SiteId.HasValue) notes = await service.GetBySiteIdAsync(q.SiteId.Value, ct);
        else if (q.AgentId.HasValue) notes = await service.GetByAgentIdAsync(q.AgentId.Value, ct);
        else notes = Array.Empty<EntityNote>();

        return Result<IReadOnlyList<NoteDto>>.Success(
            notes.Select(Map).ToList().AsReadOnly());
    }

    private static NoteDto Map(EntityNote n) => new(n.Id, n.ClientId, n.SiteId, n.AgentId,
        n.Content, n.Author, n.IsPinned, n.CreatedAt, n.UpdatedAt);
}

public sealed class ListNotesPageQueryHandler(
    INoteService service
) : IRequestHandler<ListNotesPageQuery, Result<CursorPageDto<NoteDto>>>
{
    public async Task<Result<CursorPageDto<NoteDto>>> Handle(ListNotesPageQuery q, CancellationToken ct)
    {
        var page = await service.GetPageAsync(q.ClientId, q.SiteId, q.AgentId, q.Cursor, q.Limit, ct);
        var dtos = page.Items.Select(n => new NoteDto(
            n.Id, n.ClientId, n.SiteId, n.AgentId,
            n.Content, n.Author, n.IsPinned,
            n.CreatedAt, n.UpdatedAt)).ToList();

        return Result<CursorPageDto<NoteDto>>.Success(
            new CursorPageDto<NoteDto>(
                dtos.AsReadOnly(),
                dtos.Count,
                page.Cursor,
                page.NextCursor,
                page.HasMore,
                page.Limit));
    }
}

public sealed class GetNoteByIdQueryHandler(
    INoteService service
) : IRequestHandler<GetNoteByIdQuery, Result<NoteDto>>
{
    public async Task<Result<NoteDto>> Handle(GetNoteByIdQuery q, CancellationToken ct)
    {
        var note = await service.GetByIdAsync(q.Id, ct);
        return note is null
            ? Result<NoteDto>.Failure(Error.NotFound($"Note {q.Id} not found"))
            : Result<NoteDto>.Success(new NoteDto(note.Id, note.ClientId, note.SiteId, note.AgentId,
                note.Content, note.Author, note.IsPinned, note.CreatedAt, note.UpdatedAt));
    }
}

public sealed class CreateNoteCommandHandler(
    INoteService service
) : IRequestHandler<CreateNoteCommand, Result<NoteDto>>
{
    public async Task<Result<NoteDto>> Handle(CreateNoteCommand cmd, CancellationToken ct)
    {
        var note = new EntityNote
        {
            ClientId = cmd.ClientId,
            SiteId = cmd.SiteId,
            AgentId = cmd.AgentId,
            Content = cmd.Content,
            Author = cmd.Author,
            IsPinned = cmd.IsPinned
        };
        var created = await service.CreateAsync(note, ct);
        return Result<NoteDto>.Success(new NoteDto(created.Id, created.ClientId, created.SiteId,
            created.AgentId, created.Content, created.Author, created.IsPinned,
            created.CreatedAt, created.UpdatedAt));
    }
}

public sealed class UpdateNoteCommandHandler(
    INoteService service
) : IRequestHandler<UpdateNoteCommand, Result<NoteDto>>
{
    public async Task<Result<NoteDto>> Handle(UpdateNoteCommand cmd, CancellationToken ct)
    {
        var note = await service.GetByIdAsync(cmd.Id, ct);
        if (note is null)
            return Result<NoteDto>.Failure(Error.NotFound($"Note {cmd.Id} not found"));

        if (cmd.Content is not null) note.Content = cmd.Content;
        if (cmd.Author is not null) note.Author = cmd.Author;
        if (cmd.IsPinned.HasValue) note.IsPinned = cmd.IsPinned.Value;

        await service.UpdateAsync(note, ct);
        return Result<NoteDto>.Success(new NoteDto(note.Id, note.ClientId, note.SiteId,
            note.AgentId, note.Content, note.Author, note.IsPinned,
            note.CreatedAt, note.UpdatedAt));
    }
}

public sealed class DeleteNoteCommandHandler(
    INoteService service
) : IRequestHandler<DeleteNoteCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteNoteCommand cmd, CancellationToken ct)
    {
        await service.DeleteAsync(cmd.Id, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
