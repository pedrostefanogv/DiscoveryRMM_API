using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AiChatSessionRepository : IAiChatSessionRepository
{
    private readonly MeduzaDbContext _db;

    public AiChatSessionRepository(MeduzaDbContext db) => _db = db;

    public async Task<AiChatSession> CreateAsync(AiChatSession session, CancellationToken ct = default)
    {
        session.Id = IdGenerator.NewId();
        session.CreatedAt = DateTime.UtcNow;
        session.ExpiresAt = session.CreatedAt.AddDays(180);

        _db.AiChatSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<AiChatSession?> GetByIdAsync(Guid id, Guid agentId, CancellationToken ct = default)
    {
        return await _db.AiChatSessions
            .AsNoTracking()
            .Where(s => s.Id == id && s.AgentId == agentId && s.DeletedAt == null)
            .SingleOrDefaultAsync(ct);
    }

    public async Task<List<AiChatSession>> GetByAgentAsync(Guid agentId, int limit, CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 100);

        return await _db.AiChatSessions
            .AsNoTracking()
            .Where(s => s.AgentId == agentId && s.DeletedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public async Task<List<AiChatSession>> GetExpiredAsync(DateTime cutoff, int limit, CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);

        return await _db.AiChatSessions
            .AsNoTracking()
            .Where(s => s.ExpiresAt <= cutoff && s.DeletedAt == null)
            .OrderBy(s => s.ExpiresAt)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public async Task<int> SoftDeleteAsync(Guid id, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await _db.AiChatSessions
            .Where(s => s.Id == id && s.DeletedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.DeletedAt, _ => now)
                .SetProperty(s => s.ClosedAt, s => s.ClosedAt ?? now), ct);
    }

    public async Task<int> HardDeleteAsync(DateTime deletedBefore, CancellationToken ct = default)
    {
        return await _db.AiChatSessions
            .Where(s => s.DeletedAt != null && s.DeletedAt <= deletedBefore)
            .ExecuteDeleteAsync(ct);
    }
}
