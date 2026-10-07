using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class McpToolPolicyRepository : IMcpToolPolicyRepository
{
    private readonly DiscoveryDbContext _db;

    public McpToolPolicyRepository(DiscoveryDbContext db) => _db = db;

    public async Task<IReadOnlyList<McpToolPolicy>> GetEffectivePoliciesAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        CancellationToken ct = default)
    {
        // A herança é Global -> Cliente -> Site -> Agente. Quando o escopo é um
        // agente/site, é preciso RESOLVER a cadeia (agente.SiteId e site.ClientId)
        // para que a política do cliente/site também seja herdada — sem isso, as
        // linhas de cliente/site nunca chegavam ao escopo do agente.
        var resolvedClient = clientId;
        var resolvedSite = siteId;

        if (agentId.HasValue)
        {
            var agentSite = await _db.Set<Agent>().AsNoTracking()
                .Where(a => a.Id == agentId)
                .Select(a => (Guid?)a.SiteId)
                .FirstOrDefaultAsync(ct);
            if (agentSite.HasValue)
                resolvedSite = agentSite;
        }

        if (!resolvedClient.HasValue && resolvedSite.HasValue)
        {
            var siteClient = await _db.Set<Site>().AsNoTracking()
                .Where(s => s.Id == resolvedSite)
                .Select(s => (Guid?)s.ClientId)
                .FirstOrDefaultAsync(ct);
            if (siteClient.HasValue)
                resolvedClient = siteClient;
        }

        var all = await _db.Set<McpToolPolicy>()
            .AsNoTracking()
            .Where(p =>
                (p.ClientId == null && p.SiteId == null && p.AgentId == null) ||
                (resolvedClient.HasValue && p.ClientId == resolvedClient && p.SiteId == null && p.AgentId == null) ||
                (resolvedSite.HasValue && p.SiteId == resolvedSite && p.AgentId == null) ||
                (agentId.HasValue && p.AgentId == agentId))
            .ToListAsync(ct);

        // Deduplica: para cada tool_name, mantém a política mais específica —
        // MAS uma política Locked num nível menos específico bloqueia a
        // sobrescrita pelos níveis mais específicos (bloqueio de herança).
        var result = new Dictionary<string, McpToolPolicy>(StringComparer.OrdinalIgnoreCase);
        var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var policy in all.OrderBy(p => SpecificityLevel(p, resolvedClient, resolvedSite, agentId)))
        {
            if (locked.Contains(policy.ToolName))
                continue;

            result[policy.ToolName] = policy;
            if (policy.Locked)
                locked.Add(policy.ToolName);
        }

        return result.Values.ToList();
    }

    public async Task<McpToolPolicy?> GetPolicyAsync(
        string toolName,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        CancellationToken ct = default)
    {
        var policies = await GetEffectivePoliciesAsync(clientId, siteId, agentId, ct);
        return policies.FirstOrDefault(p =>
            string.Equals(p.ToolName, toolName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<McpToolPolicy>> GetScopePoliciesAsync(
        Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
    {
        var query = ExactScope(_db.Set<McpToolPolicy>().AsNoTracking(), clientId, siteId, agentId);
        return await query.OrderBy(p => p.ToolName).ToListAsync(ct);
    }

    public async Task<McpToolPolicy> UpsertAsync(McpToolPolicy policy, CancellationToken ct = default)
    {
        policy.ToolName = (policy.ToolName ?? string.Empty).Trim();
        policy.Source = McpToolSources.Normalize(policy.Source);

        var existing = await ExactScope(_db.Set<McpToolPolicy>(), policy.ClientId, policy.SiteId, policy.AgentId)
            .FirstOrDefaultAsync(p => p.ToolName == policy.ToolName, ct);

        if (existing is null)
        {
            policy.Id = policy.Id == Guid.Empty ? Guid.NewGuid() : policy.Id;
            policy.CreatedAt = policy.CreatedAt == default ? DateTime.UtcNow : policy.CreatedAt;
            _db.Set<McpToolPolicy>().Add(policy);
            try
            {
                await _db.SaveChangesAsync(ct);
                return policy;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Corrida: outro writer criou a linha do mesmo escopo (índice
                // único ux_mcp_tool_policies_scope_tool). Aplica os valores sobre
                // a linha vencedora em vez de falhar.
                _db.Entry(policy).State = EntityState.Detached;
                var winner = await ExactScope(_db.Set<McpToolPolicy>(), policy.ClientId, policy.SiteId, policy.AgentId)
                    .FirstOrDefaultAsync(p => p.ToolName == policy.ToolName, ct);
                if (winner is null)
                    throw;

                ApplyValues(winner, policy);
                await _db.SaveChangesAsync(ct);
                return winner;
            }
        }

        ApplyValues(existing, policy);
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteScopePolicyAsync(
        string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
    {
        var tool = (toolName ?? string.Empty).Trim();
        var existing = await ExactScope(_db.Set<McpToolPolicy>(), clientId, siteId, agentId)
            .FirstOrDefaultAsync(p => p.ToolName == tool, ct);
        if (existing is null)
            return false;

        _db.Set<McpToolPolicy>().Remove(existing);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task EnsureGlobalPoliciesAsync(
        string source, IEnumerable<string> toolNames, CancellationToken ct = default)
    {
        var normalizedSource = McpToolSources.Normalize(source);
        var names = toolNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
            return;

        var existing = await _db.Set<McpToolPolicy>()
            .Where(p => p.ClientId == null && p.SiteId == null && p.AgentId == null)
            .Select(p => p.ToolName)
            .ToListAsync(ct);

        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var toCreate = names.Where(n => !existingSet.Contains(n)).ToList();
        if (toCreate.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var name in toCreate)
        {
            _db.Set<McpToolPolicy>().Add(new McpToolPolicy
            {
                Id = Guid.NewGuid(),
                ToolName = name,
                Source = normalizedSource,
                IsEnabled = true,
                MaxCallsPerMinute = normalizedSource == McpToolSources.Agent ? 10 : 5,
                TimeoutSeconds = normalizedSource == McpToolSources.Agent ? 60 : 10,
                CreatedAt = now,
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Corrida no auto-registro: a outra instância já criou as linhas.
            // As policies existentes (inclusive desabilitadas pelo operador) são
            // preservadas; basta soltar as entidades que tentamos inserir.
            foreach (var entry in _db.ChangeTracker.Entries<McpToolPolicy>()
                         .Where(e => e.State == EntityState.Added)
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    public async Task<McpToolPolicy?> GetAncestorLockedPolicyAsync(
        string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
    {
        var tool = (toolName ?? string.Empty).Trim();
        if (tool.Length == 0)
            return null;

        // Escopo global não tem ancestral.
        if (!agentId.HasValue && !siteId.HasValue && !clientId.HasValue)
            return null;

        // Resolve a cadeia (agente -> site -> cliente) como em GetEffectivePoliciesAsync.
        var resolvedClient = clientId;
        var resolvedSite = siteId;

        if (agentId.HasValue)
        {
            var agentSite = await _db.Set<Agent>().AsNoTracking()
                .Where(a => a.Id == agentId)
                .Select(a => (Guid?)a.SiteId)
                .FirstOrDefaultAsync(ct);
            if (agentSite.HasValue)
                resolvedSite = agentSite;
        }

        if (!resolvedClient.HasValue && resolvedSite.HasValue)
        {
            var siteClient = await _db.Set<Site>().AsNoTracking()
                .Where(s => s.Id == resolvedSite)
                .Select(s => (Guid?)s.ClientId)
                .FirstOrDefaultAsync(ct);
            if (siteClient.HasValue)
                resolvedClient = siteClient;
        }

        // Parent do escopo atual (um nível mais genérico).
        Guid? parentClient = null;
        Guid? parentSite = null;
        if (agentId.HasValue)
        {
            parentClient = resolvedClient;
            parentSite = resolvedSite;
        }
        else if (siteId.HasValue)
        {
            parentClient = resolvedClient;
        }

        var policies = await GetEffectivePoliciesAsync(parentClient, parentSite, null, ct);
        var policy = policies.FirstOrDefault(p =>
            string.Equals(p.ToolName, tool, StringComparison.OrdinalIgnoreCase));

        return policy is { Locked: true } ? policy : null;
    }

    /// <summary>Copia os campos configuráveis de uma política para outra.</summary>
    private static void ApplyValues(McpToolPolicy target, McpToolPolicy source)
    {
        target.IsEnabled = source.IsEnabled;
        target.MaxCallsPerMinute = source.MaxCallsPerMinute;
        target.TimeoutSeconds = source.TimeoutSeconds;
        target.Locked = source.Locked;
        target.Source = source.Source;
        if (!string.IsNullOrWhiteSpace(source.ArgumentSchemaJson))
            target.ArgumentSchemaJson = source.ArgumentSchemaJson;
        target.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Detecta violação de índice único (Postgres 23505).</summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var e = ex.InnerException; e is not null; e = e.InnerException)
        {
            var message = e.Message;
            if (message.Contains("23505", StringComparison.Ordinal) ||
                message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public async Task<int> CountLowerScopeOverridesAsync(
        string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
    {
        var tool = (toolName ?? string.Empty).Trim();
        var rows = await _db.Set<McpToolPolicy>().AsNoTracking()
            .Where(p => p.ToolName == tool
                        && (p.ClientId != null || p.SiteId != null || p.AgentId != null))
            .ToListAsync(ct);

        if (rows.Count == 0)
            return 0;

        // Nada é mais específico que uma política de agente.
        if (agentId.HasValue)
            return 0;

        if (siteId.HasValue)
        {
            var agentIds = await _db.Set<Agent>().AsNoTracking()
                .Where(a => a.SiteId == siteId)
                .Select(a => a.Id)
                .ToListAsync(ct);

            return rows.Count(p => p.AgentId.HasValue && agentIds.Contains(p.AgentId.Value));
        }

        if (clientId.HasValue)
        {
            var siteIds = await _db.Set<Site>().AsNoTracking()
                .Where(s => s.ClientId == clientId)
                .Select(s => s.Id)
                .ToListAsync(ct);

            var agentIds = await _db.Set<Agent>().AsNoTracking()
                .Where(a => siteIds.Contains(a.SiteId))
                .Select(a => a.Id)
                .ToListAsync(ct);

            return rows.Count(p =>
                (p.SiteId.HasValue && siteIds.Contains(p.SiteId.Value)) ||
                (p.AgentId.HasValue && agentIds.Contains(p.AgentId.Value)));
        }

        // Escopo global: qualquer sobrescrita de cliente/site/agente.
        return rows.Count;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetLowerScopeOverrideCountsAsync(
        IReadOnlyCollection<string> toolNames, Guid? clientId, Guid? siteId, Guid? agentId,
        CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = toolNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in names)
            counts[name] = 0;

        if (names.Count == 0)
            return counts;

        // Nada é mais específico que uma política de agente.
        if (agentId.HasValue)
            return counts;

        var rows = await _db.Set<McpToolPolicy>().AsNoTracking()
            .Where(p => names.Contains(p.ToolName)
                        && (p.ClientId != null || p.SiteId != null || p.AgentId != null))
            .Select(p => new { p.ToolName, p.ClientId, p.SiteId, p.AgentId })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return counts;

        HashSet<Guid>? siteIds = null;
        HashSet<Guid>? agentIds = null;

        if (siteId.HasValue)
        {
            agentIds = (await _db.Set<Agent>().AsNoTracking()
                    .Where(a => a.SiteId == siteId)
                    .Select(a => a.Id)
                    .ToListAsync(ct))
                .ToHashSet();
        }
        else if (clientId.HasValue)
        {
            siteIds = (await _db.Set<Site>().AsNoTracking()
                    .Where(s => s.ClientId == clientId)
                    .Select(s => s.Id)
                    .ToListAsync(ct))
                .ToHashSet();

            agentIds = (await _db.Set<Agent>().AsNoTracking()
                    .Where(a => siteIds.Contains(a.SiteId))
                    .Select(a => a.Id)
                    .ToListAsync(ct))
                .ToHashSet();
        }

        foreach (var row in rows)
        {
            var isLowerScope = siteId.HasValue
                ? row.AgentId.HasValue && agentIds!.Contains(row.AgentId.Value)
                : clientId.HasValue
                    ? (row.SiteId.HasValue && siteIds!.Contains(row.SiteId.Value)) ||
                      (row.AgentId.HasValue && agentIds!.Contains(row.AgentId.Value))
                    : true; // escopo global: qualquer linha de escopo é mais específica

            if (isLowerScope)
                counts[row.ToolName] = counts.GetValueOrDefault(row.ToolName) + 1;
        }

        return counts;
    }

    /// <summary>Filtra pela combinação EXATA de escopo (NULL conta como nível).</summary>
    private static IQueryable<McpToolPolicy> ExactScope(
        IQueryable<McpToolPolicy> query, Guid? clientId, Guid? siteId, Guid? agentId)
    {
        query = clientId.HasValue
            ? query.Where(p => p.ClientId == clientId)
            : query.Where(p => p.ClientId == null);

        query = siteId.HasValue
            ? query.Where(p => p.SiteId == siteId)
            : query.Where(p => p.SiteId == null);

        query = agentId.HasValue
            ? query.Where(p => p.AgentId == agentId)
            : query.Where(p => p.AgentId == null);

        return query;
    }

    /// <summary>
    /// Nível de especificidade: quanto maior, mais específica (agent=3, site=2, client=1, global=0).
    /// Usado para ordenar e deduplicar.
    /// </summary>
    private static int SpecificityLevel(McpToolPolicy p, Guid? clientId, Guid? siteId, Guid? agentId)
    {
        if (p.AgentId == agentId && agentId.HasValue) return 3;
        if (p.SiteId == siteId && siteId.HasValue) return 2;
        if (p.ClientId == clientId && clientId.HasValue) return 1;
        return 0; // global
    }
}
