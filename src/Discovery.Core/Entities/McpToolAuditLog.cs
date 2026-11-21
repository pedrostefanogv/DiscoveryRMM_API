namespace Discovery.Core.Entities;

/// <summary>
/// Registro de auditoria de execuções de ferramentas MCP (server-side e agent-side).
/// Permite rastrear quem/qual escopo acionou a tool, duração, status e eventuais erros.
/// </summary>
public class McpToolAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ToolName { get; set; } = string.Empty;
    /// <summary>Origem: "server" ou "agent".</summary>
    public string Origin { get; set; } = "server";
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? SessionId { get; set; }
    /// <summary>Status: success, error, denied, rate_limited, timeout.</summary>
    public string Status { get; set; } = "success";
    public int DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
