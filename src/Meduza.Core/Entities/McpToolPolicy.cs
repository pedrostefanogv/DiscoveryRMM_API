namespace Meduza.Core.Entities;

/// <summary>
/// Define allowlist e políticas de execução de tools MCP por escopo (Client/Site/Agent)
/// </summary>
public class McpToolPolicy
{
    public Guid Id { get; set; }
    
    // Scope (null = todas)
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    
    // Tool
    public string ToolName { get; set; } = string.Empty; // "filesystem.read_file", "registry.get_value", etc
    public bool IsEnabled { get; set; } = true;
    
    // Schema Validation (JSON Schema)
    public string? ArgumentSchemaJson { get; set; }
    
    // Rate Limit
    public int MaxCallsPerMinute { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 10;
    
    // Audit
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
