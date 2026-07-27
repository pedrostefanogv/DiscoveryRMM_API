using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Repositories;

public class RemoteSessionAuditRepository : IRemoteSessionAuditRepository
{
    private readonly DiscoveryDbContext _db;
    private readonly ILogger<RemoteSessionAuditRepository> _logger;

    public RemoteSessionAuditRepository(DiscoveryDbContext db, ILogger<RemoteSessionAuditRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task AddAsync(RemoteSessionAudit audit, CancellationToken ct = default)
    {
        _db.RemoteSessionAudits.Add(audit);
        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("Audit {EventType} recorded for session {SessionId}", audit.EventType, audit.RemoteSessionId);
    }

    public async Task<IEnumerable<RemoteSessionAudit>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await _db.RemoteSessionAudits
            .AsNoTracking()
            .Where(a => a.RemoteSessionId == sessionId)
            .OrderByDescending(a => a.OccurredAt)
            .ToListAsync(ct);
    }
}
