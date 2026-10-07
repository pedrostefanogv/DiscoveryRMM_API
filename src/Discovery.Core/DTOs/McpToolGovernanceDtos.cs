namespace Discovery.Core.DTOs;

/// <summary>
/// Escopo de uma política de MCP tool. Exatamente um nível é preenchido
/// (global = todos nulos), seguindo a convenção das tabelas de configuração.
/// </summary>
public record McpToolScope(Guid? ClientId, Guid? SiteId, Guid? AgentId)
{
    public bool IsGlobal => !ClientId.HasValue && !SiteId.HasValue && !AgentId.HasValue;

    public string Level =>
        AgentId.HasValue ? "agent" : SiteId.HasValue ? "site" : ClientId.HasValue ? "client" : "global";
}

/// <summary>
/// Item do catálogo de ferramentas MCP para a tela de governança: mostra o
/// estado EFETIVO (herdado) e o estado LOCAL (sobrescrito neste escopo).
/// </summary>
public record McpToolCatalogItem(
    string Name,
    string Source,
    string Description,
    bool IsEnabled,
    bool OverriddenHere,
    bool Locked,
    int MaxCallsPerMinute,
    int TimeoutSeconds,
    int LowerScopeOverrides);

/// <summary>Catálogo completo de ferramentas visíveis no escopo.</summary>
public record McpToolCatalog(McpToolScope Scope, IReadOnlyList<McpToolCatalogItem> Tools);

/// <summary>Gravação de uma política no escopo informado (null = herdar).</summary>
public record SaveMcpToolPolicyRequest(
    Guid? ClientId,
    Guid? SiteId,
    Guid? AgentId,
    bool IsEnabled,
    int? MaxCallsPerMinute,
    int? TimeoutSeconds,
    bool Locked);

/// <summary>Impacto de bloquear/desabilitar uma tool no escopo atual.</summary>
public record McpToolPolicyImpact(int LowerScopeOverrides);
