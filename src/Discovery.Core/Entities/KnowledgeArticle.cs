using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Artigo da base de conhecimento com herança de escopo: Global → Client → Site
/// ClientId=null e SiteId=null = artigo global (herdado por todos)
/// ClientId preenchido e SiteId=null = artigo do client (herdado pelos sites do client)
/// ClientId e SiteId preenchidos = artigo específico do site
///
/// Status: Draft (rascunho), Published (público), Internal (restrito ao departamento)
/// Versionamento: cada transição Draft→Published ou Draft→Internal gera snapshot em KnowledgeArticleVersion
/// </summary>
public class KnowledgeArticle
{
    public Guid Id { get; set; }

    // Escopo hierárquico — null = escopo superior
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }

    // Departamento — obrigatório quando Status = Internal
    public Guid? DepartmentId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty; // Markdown
    public string? Category { get; set; }               // string livre
    public string? TagsJson { get; set; }               // JSONB: ["tag1","tag2"]

    // Status tri-state: Draft, Published, Internal
    public string Status { get; set; } = ArticleStatus.Draft.ToString();

    // Autor original — imutável após criação
    public string? CreatedBy { get; set; }

    /// <summary>Alias para compatibilidade com reports.</summary>
    public string? Author => CreatedBy;

    // Último editor — atualizado a cada save
    public string? LastEditedBy { get; set; }
    public DateTime? LastEditedAt { get; set; }

    // Data da primeira publicação (mantida para auditoria)
    public DateTime? PublishedAt { get; set; }

    // Número da versão atual (incrementado ao publicar/internalizar)
    public int CurrentVersionNumber { get; set; } = 0;

    // Controle de chunking — null = ainda não chunkado
    public DateTime? LastChunkedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; } // soft delete

    public ICollection<KnowledgeArticleChunk> Chunks { get; set; } = new List<KnowledgeArticleChunk>();
    public ICollection<KnowledgeArticleVersion> Versions { get; set; } = new List<KnowledgeArticleVersion>();
    public ICollection<TicketKnowledgeLink> TicketLinks { get; set; } = new List<TicketKnowledgeLink>();
}
