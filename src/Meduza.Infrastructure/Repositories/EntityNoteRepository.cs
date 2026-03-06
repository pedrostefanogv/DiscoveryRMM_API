using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class EntityNoteRepository : IEntityNoteRepository
{
    private readonly MeduzaDbContext _db;

    public EntityNoteRepository(MeduzaDbContext db) => _db = db;

    public async Task<EntityNote?> GetByIdAsync(Guid id)
    {
        return await _db.EntityNotes
            .AsNoTracking()
            .SingleOrDefaultAsync(note => note.Id == id);
    }

    public async Task<IEnumerable<EntityNote>> GetByClientIdAsync(Guid clientId)
    {
        return await _db.EntityNotes
            .AsNoTracking()
            .Where(note => note.ClientId == clientId)
            .OrderByDescending(note => note.IsPinned)
            .ThenByDescending(note => note.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<EntityNote>> GetBySiteIdAsync(Guid siteId)
    {
        return await _db.EntityNotes
            .AsNoTracking()
            .Where(note => note.SiteId == siteId)
            .OrderByDescending(note => note.IsPinned)
            .ThenByDescending(note => note.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<EntityNote>> GetByAgentIdAsync(Guid agentId)
    {
        return await _db.EntityNotes
            .AsNoTracking()
            .Where(note => note.AgentId == agentId)
            .OrderByDescending(note => note.IsPinned)
            .ThenByDescending(note => note.CreatedAt)
            .ToListAsync();
    }

    public async Task<EntityNote> CreateAsync(EntityNote note)
    {
        note.Id = IdGenerator.NewId();
        note.CreatedAt = DateTime.UtcNow;
        note.UpdatedAt = DateTime.UtcNow;

        _db.EntityNotes.Add(note);
        await _db.SaveChangesAsync();

        return note;
    }

    public async Task UpdateAsync(EntityNote note)
    {
        note.UpdatedAt = DateTime.UtcNow;

        var existingNote = await _db.EntityNotes.SingleOrDefaultAsync(existing => existing.Id == note.Id);
        if (existingNote is null)
            return;

        existingNote.Content = note.Content;
        existingNote.Author = note.Author;
        existingNote.IsPinned = note.IsPinned;
        existingNote.UpdatedAt = note.UpdatedAt;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await _db.EntityNotes
            .Where(note => note.Id == id)
            .ExecuteDeleteAsync();
    }
}
