namespace Meduza.Core.Entities;

public class AiChatMessage
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    
    public int SequenceNumber { get; set; } // 1, 2, 3...
    public string Role { get; set; } = string.Empty; // "user", "assistant", "system", "tool"
    public string Content { get; set; } = string.Empty;
    
    // Metadata
    public int? TokensUsed { get; set; }
    public int? LatencyMs { get; set; }
    public string? ModelVersion { get; set; } // "gpt-4-turbo", etc
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Tool execution (se role = "tool")
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolArgumentsJson { get; set; }
    
    // Audit
    public string? TraceId { get; set; }
    
    // Relacionamentos
    public AiChatSession Session { get; set; } = null!;
}
