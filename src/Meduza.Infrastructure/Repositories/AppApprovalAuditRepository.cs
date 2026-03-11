using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AppApprovalAuditRepository : IAppApprovalAuditRepository
{
    private readonly MeduzaDbContext _db;

    public AppApprovalAuditRepository(MeduzaDbContext db) => _db = db;

    public async Task CreateAsync(AppApprovalAudit audit)
    {
        audit.Id = IdGenerator.NewId();
        audit.ChangedAt = DateTime.UtcNow;
        _db.AppApprovalAudits.Add(audit);
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<AppApprovalAudit>> GetHistoryAsync(
        AppInstallationType installationType,
        string? packageId,
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        string? changedBy,
        DateTime? changedFrom,
        DateTime? changedTo,
        AppApprovalAuditChangeType? changeType,
        int limit = 100,
        Guid? cursor = null)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var query = _db.AppApprovalAudits
            .AsNoTracking()
            .Where(x => x.InstallationType == installationType);

        if (cursor.HasValue)
            query = query.Where(x => x.Id.CompareTo(cursor.Value) < 0);

        if (!string.IsNullOrWhiteSpace(packageId))
            query = query.Where(x => x.PackageId == packageId.Trim());

        if (!string.IsNullOrWhiteSpace(changedBy))
        {
            var normalizedChangedBy = changedBy.Trim().ToLowerInvariant();
            query = query.Where(x => x.ChangedBy != null && x.ChangedBy.ToLower().Contains(normalizedChangedBy));
        }

        if (changedFrom.HasValue)
            query = query.Where(x => x.ChangedAt >= changedFrom.Value);

        if (changedTo.HasValue)
            query = query.Where(x => x.ChangedAt <= changedTo.Value);

        if (changeType.HasValue)
            query = query.Where(x => x.ChangeType == changeType.Value);

        if (scopeType.HasValue)
        {
            query = query.Where(x => x.ScopeType == scopeType.Value);
            if (scopeId.HasValue)
            {
                query = scopeType.Value switch
                {
                    AppApprovalScopeType.Client => query.Where(x => x.ClientId == scopeId.Value),
                    AppApprovalScopeType.Site => query.Where(x => x.SiteId == scopeId.Value),
                    AppApprovalScopeType.Agent => query.Where(x => x.AgentId == scopeId.Value),
                    _ => query
                };
            }
        }

        return await query
            .OrderByDescending(x => x.Id)
            .Take(safeLimit + 1)
            .ToListAsync();
    }
}
