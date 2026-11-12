using System.Collections.Concurrent;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Exceptions;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

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
    private readonly IConfigurationResolver? _configurationResolver;
    private readonly ILogger<McpToolGovernance>? _logger;

    // SEM cache próprio do teto global: o IConfigurationResolver já tem o cache
    // (Redis, 5 min) e é ELE que o ConfigurationService invalida
    // (_resolver.ClearCache()) em todos os pontos de escrita de servidor/cliente/
    // site. Um cache adicional aqui não teria como ser invalidado no save e
    // manteria o teto antigo valendo por até 30s depois de o operador baixar o
    // limite — inaceitável para um kill switch de rate limit.
    //
    // Custo: uma leitura de configuração por tool call (o resolver resolve a
    // herança e serve do Redis; é o mesmo caminho que o resto do hot path usa).

    // Fallback LOCAL do rate limit: usado quando o Redis não está configurado ou
    // está indisponível. O contador distribuído (Redis INCR + EXPIRE) é o caminho
    // normal — este dicionário existe para não deixar o chat sem limite algum.
    private static readonly ConcurrentDictionary<string, RateWindow> RateCounters = new(StringComparer.OrdinalIgnoreCase);

    private sealed class RateWindow
    {
        public int Count;
        public DateTime WindowStartUtc;
    }

    /// <summary>
    /// O resolvedor de configuração é OPCIONAL (valor default null) de propósito:
    /// o registro atual em Program.cs
    /// (<c>AddScoped&lt;IMcpToolGovernance, McpToolGovernance&gt;</c>) continua válido
    /// sem alterar o container, e os testes que constroem a governança à mão não
    /// quebram. Sem ele o teto global não é aplicável e vale só o limite por tool.
    /// </summary>
    public McpToolGovernance(
        IMcpToolPolicyRepository policies,
        IMcpToolExecutor executor,
        IRedisService? redis = null,
        IConfigurationResolver? configurationResolver = null,
        ILogger<McpToolGovernance>? logger = null)
    {
        _policies = policies;
        _executor = executor;
        _redis = redis;
        _configurationResolver = configurationResolver;
        _logger = logger;
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
        // Capacidades governáveis (ex.: A2UI) não têm handler, mas precisam
        // aparecer na tela para poder ser ligadas/desligadas por escopo.
        names.UnionWith(McpToolCatalogMetadata.GovernableCapabilities);

        // Contagem de sobrescritas em lote: uma passada para TODAS as tools
        // (antes era uma consulta por tool = N+1 no catálogo).
        var lowerScopeCounts = await _policies.GetLowerScopeOverrideCountsAsync(
            names.ToList(), scope.ClientId, scope.SiteId, scope.AgentId, ct);

        var items = new List<McpToolCatalogItem>(names.Count);
        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            effectiveByName.TryGetValue(name, out var policy);
            var isCapability = McpToolCatalogMetadata.GovernableCapabilities.Contains(name);
            var source = McpToolSources.Normalize(policy?.Source
                         ?? ((serverByName.ContainsKey(name) || isCapability) ? McpToolSources.Server : McpToolSources.Agent));
            var defaults = DefaultsFor(source);
            var metadata = McpToolCatalogMetadata.For(name, source);

            items.Add(new McpToolCatalogItem(
                Name: name,
                Source: source,
                Description: McpToolCatalogMetadata.CapabilityDescription(name)
                             ?? ResolveDescription(name, source, serverByName, policy, globalDescriptions),
                IsEnabled: policy?.IsEnabled ?? true,
                OverriddenHere: localNames.Contains(name),
                Locked: policy?.Locked ?? false,
                // Capacidade não executável: rate limit e timeout não existem —
                // mostrar "10s" ou "60 chamadas/min" só confundiria o operador.
                MaxCallsPerMinute: isCapability ? 0 : (policy?.MaxCallsPerMinute ?? defaults.Max),
                TimeoutSeconds: isCapability
                               ? 0
                               : (policy?.TimeoutSeconds
                                  ?? (metadata.TimeoutApplies ? metadata.RecommendedTimeoutSeconds : defaults.Timeout)),
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

        // Capacidades governáveis (ex.: a2ui) não têm handler, mas são do
        // SERVIDOR: sem este caso, o primeiro "salvar" gravava Source=agent e a
        // tela passava a exibir a capacidade como ferramenta do agente.
        var isCapability = McpToolCatalogMetadata.GovernableCapabilities.Contains(toolName);
        var source = inherited?.Source
                     ?? ((isCapability || _executor.GetServerCatalog().Any(t =>
                             string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase)))
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

    /// <inheritdoc />
    public async Task<int> ResolveMaxCallsPerMinuteAsync(
        McpToolScope scope, int policyMaxCallsPerMinute, CancellationToken ct = default)
    {
        var policyMax = McpToolRateLimitCatalog.Normalize(policyMaxCallsPerMinute);
        var ceiling = await ResolveGlobalCeilingAsync(scope, ct);

        // Regra do MIN num ÚNICO ponto (McpToolRateLimitCatalog.Effective), o
        // mesmo usado pelo McpToolExecutor: sem teto global aplicável vale o
        // limite da tool — o teto nunca o AUMENTA.
        return McpToolRateLimitCatalog.Effective(policyMax, ceiling);
    }

    /// <summary>
    /// Teto global herdável efetivo do escopo (servidor → cliente → site), ou 0
    /// quando não há resolvedor/configuração — caso em que o teto é ignorado e o
    /// limite por tool vale integralmente.
    ///
    /// Escopo de AGENTE não tem nível de configuração próprio: usa o site do
    /// agente, caindo para o cliente quando o agente não tem site. Quem resolve a
    /// cadeia agente → site → cliente é o repositório de políticas
    /// (<c>ResolveAgentScopeAsync</c>) — as policies do agente já precisam dessa
    /// mesma resolução, então ela é feita uma única vez e não pode divergir.
    ///
    /// Um escopo que não resolva para site NEM para cliente (ex.: só agentId e o
    /// agente não tem site) cai no nível SERVIDOR — nunca em 0, que desligaria o
    /// teto silenciosamente.
    /// </summary>
    private async Task<int> ResolveGlobalCeilingAsync(McpToolScope scope, CancellationToken ct)
    {
        if (_configurationResolver is null)
            return 0;

        var (siteId, clientId) = await _policies.ResolveAgentScopeAsync(
            scope.ClientId, scope.SiteId, scope.AgentId, ct);

        int ceiling;
        try
        {
            // ResolveForSiteAsync/ResolveForClientAsync JÁ aplicam a herança
            // (site ?? cliente ?? servidor), então o valor que volta é o final —
            // só falta sanitizar. O caminho do SERVIDOR é a exceção: ali a
            // herança não foi aplicada por ninguém e o helper faz isso.
            if (siteId.HasValue)
            {
                var resolved = await _configurationResolver.ResolveForSiteAsync(siteId.Value);
                ceiling = McpToolRateLimitCatalog.Normalize(resolved.McpToolCallsPerMinute);
            }
            else if (clientId.HasValue)
            {
                var resolved = await _configurationResolver.ResolveForClientAsync(clientId.Value);
                ceiling = McpToolRateLimitCatalog.Normalize(resolved.McpToolCallsPerMinute);
            }
            else
            {
                var server = await _configurationResolver.GetServerAsync();
                ceiling = ConfigurationResolver.ResolveMcpToolCallsPerMinute(
                    null, null, server.McpToolCallsPerMinute);
            }
        }
        catch (Exception ex)
        {
            // Fail-open consciente: uma falha de leitura da configuração não pode
            // derrubar o chat nem desligar o limite por tool (que continua valendo).
            // Logado em WARNING: a degradação (fica só o limite por tool, sem teto
            // global) não pode ser silenciosa.
            _logger?.LogWarning(ex,
                "Falha ao resolver o teto global de chamadas de tools MCP (escopo site={SiteId} client={ClientId}); usando apenas o limite por tool.",
                siteId, clientId);
            return 0;
        }

        // NÃO normalizar aqui: o valor 0 é o sentinela de "sem teto global
        // aplicável" e Normalize(0) viraria 20, criando um piso artificial onde
        // não existe configuração (justamente o que o produto proíbe: o teto é um
        // piso, nunca um limite novo). A sanitização do valor lido da configuração
        // já aconteceu em ResolveMcpToolCallsPerMinute; um resolvedor que devolva
        // <= 0 é tratado como ausente.
        return ceiling <= 0 ? 0 : ceiling;
    }

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
    /// Padrões usados quando não há política no escopo: rate limit e timeout de
    /// fallback. O rate limit é o MESMO para servidor e agente
    /// (<see cref="McpToolRateLimitCatalog.DefaultCallsPerMinute"/> = 20/min),
    /// dentro do teto global herdável das configurações — antes eram 5 no
    /// servidor e 10 no agente, números que não correspondiam a nenhuma decisão
    /// de produto. O timeout de fallback do agente é 120s porque operações comuns
    /// (winget, inventário, updates) estouram 60s; tools específicas recebem
    /// recomendações próprias (McpToolCatalogMetadata).
    /// </summary>
    private static (int Max, int Timeout) DefaultsFor(string source) =>
        string.Equals(source, McpToolSources.Agent, StringComparison.OrdinalIgnoreCase)
            ? (McpToolRateLimitCatalog.DefaultCallsPerMinute, 120)
            : (McpToolRateLimitCatalog.DefaultCallsPerMinute, 30);
}
