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
        // Busca todas as políticas que são globais OU batem com o escopo
        // Prioridade: agent > site > client > global (já ordenamos pelo nível de especificidade)
        var all = await _db.Set<McpToolPolicy>()
            .AsNoTracking()
            .Where(p =>
                // Global (todos NULL)
                (p.ClientId == null && p.SiteId == null && p.AgentId == null) ||
                // Match por client
                (p.ClientId == clientId && p.SiteId == null && p.AgentId == null) ||
                // Match por site
                (p.SiteId == siteId && p.AgentId == null) ||
                // Match por agent
                (p.AgentId == agentId))
            .ToListAsync(ct);

        // Deduplica: para cada tool_name, mantém a política mais específica
        var result = new Dictionary<string, McpToolPolicy>(StringComparer.OrdinalIgnoreCase);

        foreach (var policy in all.OrderBy(p => SpecificityLevel(p, clientId, siteId, agentId)))
        {
            result[policy.ToolName] = policy;
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
