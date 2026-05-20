namespace Discovery.Core.Entities;

/// <summary>
/// Snapshot imutável de uma versão publicada/interna do artigo.
/// Cada transição Draft→Published ou Draft→Internal gera uma nova versão.
/// Edições enquanto Draft NÃO geram versão — alteram o registro diretamente.
/// </summary>
public class KnowledgeArticleVersion
{
    public Guid Id { get; set; }
    public Guid ArticleId { get; set; }

    public int VersionNumber { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? TagsJson { get; set; }
    public string Status { get; set; } = string.Empty; // "Published" ou "Internal"

    public string? EditedBy { get; set; }
    public string? ChangeSummary { get; set; }

    public DateTime CreatedAt { get; set; }

    public KnowledgeArticle Article { get; set; } = null!;
}
