using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class RemoteSessionRepository : IRemoteSessionRepository
{
    private readonly DiscoveryDbContext _db;

    public RemoteSessionRepository(DiscoveryDbContext db) => _db = db;

    public async Task<RemoteSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.RemoteSessions
            .Include(s => s.Recording)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IEnumerable<RemoteSession>> GetActiveByAgentAsync(Guid agentId, CancellationToken ct = default)
        => await _db.RemoteSessions
            .AsNoTracking()
            .Where(s => s.AgentId == agentId && s.Status == "active")
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync(ct);

    public async Task<IEnumerable<RemoteSession>> GetActiveByUserAsync(Guid userId, CancellationToken ct = default)
        => await _db.RemoteSessions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.Status == "active")
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync(ct);

    public async Task<int> CountActiveByAgentAsync(Guid agentId, CancellationToken ct = default)
        => await _db.RemoteSessions
            .AsNoTracking()
            .CountAsync(s => s.AgentId == agentId && s.Status == "active", ct);

    public async Task<int> CountActiveByUserAsync(Guid userId, CancellationToken ct = default)
        => await _db.RemoteSessions
            .AsNoTracking()
            .CountAsync(s => s.UserId == userId && s.Status == "active", ct);

    public async Task<RemoteSession> CreateAsync(RemoteSession session, CancellationToken ct = default)
    {
        _db.RemoteSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<RemoteSession> UpdateAsync(RemoteSession session, CancellationToken ct = default)
    {
        // Evita conflito de tracking quando a entidade já está sendo rastreada
        // (ex: criada via CreateAsync no mesmo escopo e depois atualizada via SetNatsSubjectAsync/CloseSessionAsync)
        var alreadyTracked = _db.ChangeTracker.Entries<RemoteSession>()
            .FirstOrDefault(e => e.Entity.Id == session.Id);

        if (alreadyTracked != null)
        {
            alreadyTracked.CurrentValues.SetValues(session);
        }
        else
        {
            _db.RemoteSessions.Update(session);
        }

        await _db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<IEnumerable<RemoteSession>> GetExpiredAsync(DateTime before, CancellationToken ct = default)
        => await _db.RemoteSessions
            .AsNoTracking()
            .Where(s => s.Status == "active" && s.ExpiresAt < before)
            .ToListAsync(ct);
}
