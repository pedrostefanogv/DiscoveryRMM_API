namespace Meduza.Core.Entities;

/// <summary>
/// Artigo da base de conhecimento com herança de escopo: Global → Client → Site
/// ClientId=null e SiteId=null = artigo global (herdado por todos)
/// ClientId preenchido e SiteId=null = artigo do client (herdado pelos sites do client)
/// ClientId e SiteId preenchidos = artigo específico do site
/// </summary>
public class KnowledgeArticle
{
    public Guid Id { get; set; }

    // Escopo hierárquico — null = escopo superior
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty; // Markdown
    public string? Category { get; set; }               // string livre
    public string? TagsJson { get; set; }               // JSONB: ["tag1","tag2"]
    public string? Author { get; set; }

    public bool IsPublished { get; set; } = false;
    public DateTime? PublishedAt { get; set; }

    // Controle de chunking — null = ainda não chunkado
    public DateTime? LastChunkedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; } // soft delete

    public ICollection<KnowledgeArticleChunk> Chunks { get; set; } = new List<KnowledgeArticleChunk>();
    public ICollection<TicketKnowledgeLink> TicketLinks { get; set; } = new List<TicketKnowledgeLink>();
}
