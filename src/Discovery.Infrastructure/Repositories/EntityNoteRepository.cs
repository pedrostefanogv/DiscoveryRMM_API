using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class EntityNoteRepository : IEntityNoteRepository
{
    private readonly DiscoveryDbContext _db;

    public EntityNoteRepository(DiscoveryDbContext db) => _db = db;

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

    public async Task<CursorPageDto<EntityNote>> GetPageAsync(Guid? clientId, Guid? siteId, Guid? agentId, string? cursor, int limit)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        var query = _db.EntityNotes.AsNoTracking().AsQueryable();

        if (clientId.HasValue)
            query = query.Where(note => note.ClientId == clientId.Value);
        if (siteId.HasValue)
            query = query.Where(note => note.SiteId == siteId.Value);
        if (agentId.HasValue)
            query = query.Where(note => note.AgentId == agentId.Value);

        if (CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorCreatedAtUtc, out var cursorId))
        {
            query = CursorPaginationHelper.ApplyCreatedAtCursor(
                query, cursorCreatedAtUtc, cursorId,
                note => note.CreatedAt, note => note.Id);
        }

        var items = await query
            .OrderByDescending(note => note.CreatedAt)
            .ThenByDescending(note => note.Id)
            .Take(safeLimit + 1)
            .ToListAsync();

        var slice = CursorPaginationHelper.SlicePage<EntityNote>(items, safeLimit);
        return new CursorPageDto<EntityNote>(
            slice.Page,
            slice.Page.Count,
            cursor,
            slice.HasMore && slice.LastItem is not null
                ? CursorPaginationHelper.EncodeCreatedAtCursor(slice.LastItem.CreatedAt, slice.LastItem.Id)
                : null,
            slice.HasMore,
            safeLimit);
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
