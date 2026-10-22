using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Repositories;

public class ConfigurationAuditRepository : IConfigurationAuditRepository
{
    private readonly DiscoveryDbContext _db;
    private readonly IAuditChainService? _chain;
    private readonly ILogger<ConfigurationAuditRepository>? _logger;

    public ConfigurationAuditRepository(
        DiscoveryDbContext db,
        IAuditChainService? chain = null,
        ILogger<ConfigurationAuditRepository>? logger = null)
    {
        _db = db;
        _chain = chain;
        _logger = logger;
    }

    public async Task CreateAsync(ConfigurationAudit audit)
    {
        audit.Id = IdGenerator.NewId();
        audit.ChangedAt = DateTime.UtcNow;

        await PopulateChainAsync(audit);

        _db.ConfigurationAudits.Add(audit);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// HOOK M241: popula prev_hash/entry_hash. Fail-open: se o chain falhar,
    /// grava com hashes nulos e loga warning (disponibilidade > prova).
    /// </summary>
    private async Task PopulateChainAsync(ConfigurationAudit audit)
    {
        if (_chain is null)
            return;

        try
        {
            var payloadHash = AuditChainService.ComputeConfigurationPayloadHash(audit);
            var (prev, entry) = await _chain.AppendAsync(AuditChainService.ScopeConfigurationAudits, payloadHash);
            audit.PrevHash = prev;
            audit.EntryHash = entry;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AuditChain: hook falhou para configuration_audits — gravando sem hash");
            audit.PrevHash = null;
            audit.EntryHash = null;
        }
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetByEntityAsync(string entityType, Guid entityId, int limit = 100)
    {
        if (!Enum.TryParse<ConfigurationEntityType>(entityType, true, out var parsedType))
            return [];

        var safeLimit = Math.Clamp(limit, 1, 5000);

        return await _db.ConfigurationAudits
            .AsNoTracking()
            .Where(audit => audit.EntityType == parsedType && audit.EntityId == entityId)
            .OrderByDescending(audit => audit.ChangedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetRecentAsync(int days = 90, int limit = 1000)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var safeLimit = Math.Clamp(limit, 1, 5000);

        return await _db.ConfigurationAudits
            .AsNoTracking()
            .Where(audit => audit.ChangedAt >= since)
            .OrderByDescending(audit => audit.ChangedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetByUserAsync(string username, int limit = 100)
    {
        var safeLimit = Math.Clamp(limit, 1, 5000);

        return await _db.ConfigurationAudits
            .AsNoTracking()
            .Where(audit => audit.ChangedBy == username)
            .OrderByDescending(audit => audit.ChangedAt)
            .Take(safeLimit)
            .ToListAsync();
    }
}
