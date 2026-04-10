namespace Discovery.Core.Entities;

public class KnowledgeEmbeddingQueueItem
{
    public Guid Id { get; set; }
    public Guid ArticleId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime AvailableAt { get; set; }
    public string? LastError { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
