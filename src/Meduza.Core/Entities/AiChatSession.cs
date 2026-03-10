namespace Meduza.Core.Entities;

public class AiChatSession
{
    public Guid Id { get; set; }
    
    // Contexto
    public Guid AgentId { get; set; }
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }
    
    // Metadata
    public string? Topic { get; set; } // "troubleshooting", "advisory", etc
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    
    // Audit
    public string CreatedByIp { get; set; } = string.Empty;
    public string? TraceId { get; set; }
    
    // Retenção
    public DateTime ExpiresAt { get; set; } // CreatedAt + 180 dias
    public DateTime? DeletedAt { get; set; } // Soft delete
    
    // Relacionamentos
    public Agent Agent { get; set; } = null!;
    public ICollection<AiChatMessage> Messages { get; set; } = new List<AiChatMessage>();
}
