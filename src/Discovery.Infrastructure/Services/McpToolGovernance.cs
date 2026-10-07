using System.Collections.Concurrent;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Exceptions;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação da governança de MCP tools.
///
/// O catálogo é montado a partir de duas fontes:
///  • tools de SERVIDOR — handlers registrados no McpToolExecutor;
///  • tools de AGENTE — linhas de mcp_tool_policies com source='agent',
///    criadas no auto-registro do agente (RegisterAgentToolsAsync).
///
/// Herança: a ausência de linha no escopo significa "herdar do nível acima".
/// Efetivo = política mais específica; Locked num nível interrompe a sobrescrita
/// dos níveis mais específicos (mesma semântica dos campos bloqueados das
/// configurações globais).
/// </summary>
public class McpToolGovernance : IMcpToolGovernance
{
    private readonly IMcpToolPolicyRepository _policies;
    private readonly IMcpToolExecutor _executor;
    private readonly IRedisService? _redis;

    // Fallback LOCAL do rate limit: usado quando o Redis não está configurado ou
    // está indisponível. O contador distribuído (Redis INCR + EXPIRE) é o caminho
    // normal — este dicionário existe para não deixar o chat sem limite algum.
    private static readonly ConcurrentDictionary<string, RateWindow> RateCounters = new(StringComparer.OrdinalIgnoreCase);

    private sealed class RateWindow
    {
        public int Count;
        public DateTime WindowStartUtc;
    }

    public McpToolGovernance(
        IMcpToolPolicyRepository policies,
        IMcpToolExecutor executor,
        IRedisService? redis = null)
    {
        _policies = policies;
        _executor = executor;
        _redis = redis;
    }

    public Task<IReadOnlyList<McpToolPolicy>> GetEffectivePoliciesAsync(McpToolScope scope, CancellationToken ct = default) =>
        _policies.GetEffectivePoliciesAsync(scope.ClientId, scope.SiteId, scope.AgentId, ct);

    public async Task<McpToolCatalog> GetCatalogAsync(McpToolScope scope, CancellationToken ct = default)
    {
        var effective = await _policies.GetEffectivePoliciesAsync(scope.ClientId, scope.SiteId, scope.AgentId, ct);
        var local = await _policies.GetScopePoliciesAsync(scope.ClientId, scope.SiteId, scope.AgentId, ct);
        var effectiveByName = effective.ToDictionary(p => p.ToolName, StringComparer.OrdinalIgnoreCase);

        // Descrições registradas pelo agente ficam nas policies globais. Uma
        // sobrescrita de cliente/site/agente não guarda descrição, então o texto
        // dela é recuperado daqui para a tela nunca mostrar o genérico.
        var globalDescriptions = (await _policies.GetScopePoliciesAsync(null, null, null, ct))
            .Where(p => !string.IsNullOrWhiteSpace(p.Description))
            .GroupBy(p => p.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Description!, StringComparer.OrdinalIgnoreCase);
        var localNames = new HashSet<string>(local.Select(p => p.ToolName), StringComparer.OrdinalIgnoreCase);
        var serverByName = _executor.GetServerCatalog()
            .ToDictionary(t => t.Name, t => t.Description, StringComparer.OrdinalIgnoreCase);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(serverByName.Keys);
        names.UnionWith(effectiveByName.Keys);
        names.UnionWith(localNames);

        // Contagem de sobrescritas em lote: uma passada para TODAS as tools
        // (antes era uma consulta por tool = N+1 no catálogo).
        var lowerScopeCounts = await _policies.GetLowerScopeOverrideCountsAsync(
            names.ToList(), scope.ClientId, scope.SiteId, scope.AgentId, ct);

        var items = new List<McpToolCatalogItem>(names.Count);
        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            effectiveByName.TryGetValue(name, out var policy);
            var source = McpToolSources.Normalize(policy?.Source
                         ?? (serverByName.ContainsKey(name) ? McpToolSources.Server : McpToolSources.Agent));
            var defaults = DefaultsFor(source);
            var metadata = McpToolCatalogMetadata.For(name, source);

            items.Add(new McpToolCatalogItem(
                Name: name,
                Source: source,
                Description: ResolveDescription(name, source, serverByName, policy, globalDescriptions),
                IsEnabled: policy?.IsEnabled ?? true,
                OverriddenHere: localNames.Contains(name),
                Locked: policy?.Locked ?? false,
                MaxCallsPerMinute: policy?.MaxCallsPerMinute ?? defaults.Max,
                TimeoutSeconds: policy?.TimeoutSeconds
                               ?? (metadata.TimeoutApplies ? metadata.RecommendedTimeoutSeconds : defaults.Timeout),
                LowerScopeOverrides: lowerScopeCounts.GetValueOrDefault(name),
                Category: metadata.Category,
                WhenToUse: metadata.WhenToUse,
                RecommendedTimeoutSeconds: metadata.RecommendedTimeoutSeconds,
                TimeoutApplies: metadata.TimeoutApplies));
        }

        return new McpToolCatalog(scope, items);
    }

    public async Task<McpToolCatalog> SavePolicyAsync(
        string toolName, SaveMcpToolPolicyRequest request, CancellationToken ct = default)
    {
        var scope = new McpToolScope(request.ClientId, request.SiteId, request.AgentId);

        // Bloqueio de herança: um ancestral com Locked=true não pode ser
        // sobrescrito por este escopo (o controller devolve 409).
        var lockedAncestor = await _policies.GetAncestorLockedPolicyAsync(
            toolName, scope.ClientId, scope.SiteId, scope.AgentId, ct);
        if (lockedAncestor is not null)
            throw new McpToolPolicyLockedException(toolName);

        var effective = await _policies.GetEffectivePoliciesAsync(scope.ClientId, scope.SiteId, scope.AgentId, ct);
        var inherited = effective.FirstOrDefault(p =>
            string.Equals(p.ToolName, toolName, StringComparison.OrdinalIgnoreCase));

        var source = inherited?.Source
                     ?? (_executor.GetServerCatalog().Any(t =>
                             string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase))
                         ? McpToolSources.Server
                         : McpToolSources.Agent);

        var defaults = DefaultsFor(source);

        await _policies.UpsertAsync(new McpToolPolicy
        {
            ToolName = toolName,
            ClientId = scope.ClientId,
            SiteId = scope.SiteId,
            AgentId = scope.AgentId,
            Source = source,
            IsEnabled = request.IsEnabled,
            Locked = request.Locked,
            // Herda a descrição registrada pelo agente para que a sobrescrita no
            // escopo não perca a explicação da ferramenta.
            Description = inherited?.Description,
            MaxCallsPerMinute = request.MaxCallsPerMinute is > 0 ? request.MaxCallsPerMinute.Value : defaults.Max,
            TimeoutSeconds = request.TimeoutSeconds is > 0 ? request.TimeoutSeconds.Value : defaults.Timeout,
        }, ct);

        return await GetCatalogAsync(scope, ct);
    }

    public Task<bool> ResetPolicyAsync(string toolName, McpToolScope scope, CancellationToken ct = default) =>
        _policies.DeleteScopePolicyAsync(toolName, scope.ClientId, scope.SiteId, scope.AgentId, ct);

    public Task<int> GetImpactAsync(string toolName, McpToolScope scope, CancellationToken ct = default) =>
        _policies.CountLowerScopeOverridesAsync(toolName, scope.ClientId, scope.SiteId, scope.AgentId, ct);

    public async Task<bool> TryConsumeRateLimitAsync(string toolName, McpToolScope scope, int maxCallsPerMinute)
    {
        if (maxCallsPerMinute <= 0)
            return true;

        var key = BuildRateLimitKey(toolName, scope);

        // Caminho normal: contador distribuído no Redis (INCR + EXPIRE 60s).
        // IncrementAsync devolve 0 quando o Redis está indisponível — nesse caso
        // caímos no contador local para não perder o limite silenciosamente.
        if (_redis is not null)
        {
            var count = await _redis.IncrementAsync(key);
            if (count > 0)
            {
                // Garante o TTL na primeira chamada E sempre que a tool já está
                // bloqueada: se o processo morrer entre o INCR e o EXPIRE, a chave
                // ficaria sem expiração e a tool nunca mais seria liberada.
                if (count == 1 || count > maxCallsPerMinute)
                    await _redis.SetExpiryAsync(key, 60);
                return count <= maxCallsPerMinute;
            }
        }

        return ConsumeLocalRateLimit(key, maxCallsPerMinute);
    }

    /// <summary>Chave do rate limit por tool + escopo (uma janela de 60s).</summary>
    private static string BuildRateLimitKey(string toolName, McpToolScope scope) =>
        $"mcp:ratelimit:{toolName}:{scope.ClientId}:{scope.SiteId}:{scope.AgentId}";

    /// <summary>Fallback local (processo) do rate limit — janela fixa de 60s.</summary>
    private static bool ConsumeLocalRateLimit(string key, int maxCallsPerMinute)
    {
        var now = DateTime.UtcNow;
        var window = RateCounters.GetOrAdd(key, _ => new RateWindow { Count = 0, WindowStartUtc = now });

        lock (window)
        {
            if (now - window.WindowStartUtc >= TimeSpan.FromMinutes(1))
            {
                window.Count = 0;
                window.WindowStartUtc = now;
            }

            window.Count++;
            return window.Count <= maxCallsPerMinute;
        }
    }

    /// <summary>
    /// Descrição exibida na tela: tools de servidor usam o texto curado do
    /// executor; tools de agente usam a descrição registrada pelo agente
    /// (persistida em mcp_tool_policies.description) e só caem no texto genérico
    /// quando ela não existe.
    /// </summary>
    private static string ResolveDescription(
        string name,
        string source,
        IReadOnlyDictionary<string, string> serverByName,
        McpToolPolicy? policy,
        IReadOnlyDictionary<string, string> globalDescriptions)
    {
        if (serverByName.TryGetValue(name, out var serverDesc) && !string.IsNullOrWhiteSpace(serverDesc))
            return serverDesc;

        if (!string.IsNullOrWhiteSpace(policy?.Description))
            return policy!.Description!;

        if (globalDescriptions.TryGetValue(name, out var globalDesc) && !string.IsNullOrWhiteSpace(globalDesc))
            return globalDesc;

        return string.Equals(source, McpToolSources.Agent, StringComparison.OrdinalIgnoreCase)
            ? $"Ferramenta do agente executada na máquina do cliente ({name})."
            : $"Executa a tool '{name}'.";
    }

    /// <summary>
    /// Padrões usados quando não há política no escopo: rate limit por origem e
    /// timeout de fallback. O timeout de fallback do agente subiu de 60s para
    /// 120s porque operações comuns (winget, inventário, updates) estouram 60s;
    /// tools específicas recebem recomendações próprias (McpToolCatalogMetadata).
    /// </summary>
    private static (int Max, int Timeout) DefaultsFor(string source) =>
        string.Equals(source, McpToolSources.Agent, StringComparison.OrdinalIgnoreCase)
            ? (10, 120)
            : (5, 30);
}
