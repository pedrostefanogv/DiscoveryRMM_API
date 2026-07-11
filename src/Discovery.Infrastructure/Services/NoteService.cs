using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public sealed class NoteService : INoteService
{
    private readonly IEntityNoteRepository _repo;

    public NoteService(IEntityNoteRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<EntityNote>> GetByClientIdAsync(Guid clientId, CancellationToken ct = default)
    {
        var notes = await _repo.GetByClientIdAsync(clientId);
        return notes.ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<EntityNote>> GetBySiteIdAsync(Guid siteId, CancellationToken ct = default)
    {
        var notes = await _repo.GetBySiteIdAsync(siteId);
        return notes.ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<EntityNote>> GetByAgentIdAsync(Guid agentId, CancellationToken ct = default)
    {
        var notes = await _repo.GetByAgentIdAsync(agentId);
        return notes.ToList().AsReadOnly();
    }

    public Task<CursorPageDto<EntityNote>> GetPageAsync(Guid? clientId, Guid? siteId, Guid? agentId, string? cursor, int limit, CancellationToken ct = default)
    {
        _ = ct;
        return _repo.GetPageAsync(clientId, siteId, agentId, cursor, limit);
    }

    public Task<EntityNote?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _repo.GetByIdAsync(id);

    public Task<EntityNote> CreateAsync(EntityNote note, CancellationToken ct = default)
    {
        note.Id = Guid.NewGuid();
        note.CreatedAt = DateTime.UtcNow;
        note.UpdatedAt = DateTime.UtcNow;
        return _repo.CreateAsync(note);
    }

    public Task UpdateAsync(EntityNote note, CancellationToken ct = default)
    {
        note.UpdatedAt = DateTime.UtcNow;
        return _repo.UpdateAsync(note);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
        => _repo.DeleteAsync(id);
}
