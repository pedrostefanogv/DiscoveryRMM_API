namespace Discovery.Core.Entities;

public class AiChatJob
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid AgentId { get; set; }
    
    public string Status { get; set; } = "Pending"; // Pending, Processing, Completed, Failed, Timeout
    public string UserMessage { get; set; } = string.Empty;
    public string? AssistantMessage { get; set; }
    public int? TokensUsed { get; set; }
    public string? ErrorMessage { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    
    public string? TraceId { get; set; }
}
