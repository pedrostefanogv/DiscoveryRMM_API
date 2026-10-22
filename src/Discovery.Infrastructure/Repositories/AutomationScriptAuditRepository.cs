using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Repositories;

public class AutomationScriptAuditRepository : IAutomationScriptAuditRepository
{
    private readonly DiscoveryDbContext _db;
    private readonly IAuditChainService? _chain;
    private readonly ILogger<AutomationScriptAuditRepository>? _logger;

    public AutomationScriptAuditRepository(
        DiscoveryDbContext db,
        IAuditChainService? chain = null,
        ILogger<AutomationScriptAuditRepository>? logger = null)
    {
        _db = db;
        _chain = chain;
        _logger = logger;
    }

    public async Task CreateAsync(AutomationScriptAudit audit)
    {
        audit.Id = IdGenerator.NewId();
        audit.ChangedAt = DateTime.UtcNow;

        await PopulateChainAsync(audit);

        _db.AutomationScriptAudits.Add(audit);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// HOOK M241: popula prev_hash/entry_hash. Fail-open: se o chain falhar,
    /// grava com hashes nulos e loga warning (disponibilidade > prova).
    /// </summary>
    private async Task PopulateChainAsync(AutomationScriptAudit audit)
    {
        if (_chain is null)
            return;

        try
        {
            var payloadHash = AuditChainService.ComputeScriptPayloadHash(audit);
            var (prev, entry) = await _chain.AppendAsync(AuditChainService.ScopeAutomationScriptAudits, payloadHash);
            audit.PrevHash = prev;
            audit.EntryHash = entry;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AuditChain: hook falhou para automation_script_audits — gravando sem hash");
            audit.PrevHash = null;
            audit.EntryHash = null;
        }
    }

    public async Task<IReadOnlyList<AutomationScriptAudit>> GetByScriptIdAsync(Guid scriptId, int limit = 100)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);

        return await _db.AutomationScriptAudits
            .AsNoTracking()
            .Where(x => x.ScriptId == scriptId)
            .OrderByDescending(x => x.ChangedAt)
            .Take(safeLimit)
            .ToListAsync();
    }
}
