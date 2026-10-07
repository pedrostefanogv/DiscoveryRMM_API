namespace Discovery.Core.Entities;

/// <summary>
/// Política de autorização para MCP tools por escopo (client/site/agent).
/// Criada pela migration M045 — agora efetivamente utilizada pelo McpToolExecutor.
/// </summary>
public class McpToolPolicy
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string? ArgumentSchemaJson { get; set; }

    /// <summary>
    /// Descrição da tool exibida na tela de governança. Para tools de agente é a
    /// descrição registrada no startup (persistida aqui para que o catálogo
    /// explique cada ferramenta mesmo em escopo global, sem depender do cache do
    /// agente). Para tools de servidor a descrição vem do executor.
    /// </summary>
    public string? Description { get; set; }
    public int MaxCallsPerMinute { get; set; } = 5;

    /// <summary>
    /// Timeout da execução em segundos (0 = sem limite). O padrão do modelo
    /// acompanha o fallback do servidor (30s) — tools específicas recebem
    /// recomendações próprias via <c>McpToolCatalogMetadata</c>.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Origem da tool: "server" (handler no McpToolExecutor) ou "agent"
    /// (tool registrada pelo agente, executada na máquina do cliente).
    /// </summary>
    public string Source { get; set; } = McpToolSources.Server;

    /// <summary>
    /// Quando true, escopos mais específicos (site/agent) NÃO podem sobrescrever
    /// esta política — mesma semântica de "campos bloqueados para herança" das
    /// configurações globais.
    /// </summary>
    public bool Locked { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Origens de uma MCP tool: handler no servidor x tool registrada pelo agente.
/// </summary>
public static class McpToolSources
{
    public const string Server = "server";
    public const string Agent = "agent";

    public static bool IsValid(string? source) =>
        string.Equals(source, Server, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, Agent, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? source) =>
        string.Equals(source, Agent, StringComparison.OrdinalIgnoreCase) ? Agent : Server;
}
