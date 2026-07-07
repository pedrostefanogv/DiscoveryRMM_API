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
    public int MaxCallsPerMinute { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 10;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
