namespace Discovery.Core.Entities;

/// <summary>
/// Sub-página interna de um artigo da base de conhecimento (estilo Notion).
///
/// Diferente do modelo anterior (que vinculava artigos entre si via parent_id),
/// esta entidade representa as "partes/páginas" DENTRO de um único artigo.
/// Um artigo pode ser dividido em várias sub-páginas aninhadas (até 3 níveis),
/// todas pertencentes ao mesmo artigo (ArticleId).
///
/// Exemplo:
///   Artigo "Manual de TI"
///     ├─ Página "Hardware"          (ParentPageId = null)
///     │   ├─ Página "Impressoras"   (ParentPageId = Hardware)
///     │   │   └─ Página "Drivers"   (ParentPageId = Impressoras)
///     │   └─ Página "Monitores"     (ParentPageId = Hardware)
///     └─ Página "Software"          (ParentPageId = null)
///
/// As sub-páginas herdam escopo e status do artigo pai (não têm escopo próprio).
/// </summary>
public class KnowledgeArticlePage
{
    public Guid Id { get; set; }

    /// <summary>Artigo ao qual esta sub-página pertence.</summary>
    public Guid ArticleId { get; set; }
    public KnowledgeArticle Article { get; set; } = null!;

    /// <summary>Sub-página pai (null = página de nível 1 dentro do artigo).</summary>
    public Guid? ParentPageId { get; set; }
    public KnowledgeArticlePage? ParentPage { get; set; }
    public ICollection<KnowledgeArticlePage> Children { get; set; } = new List<KnowledgeArticlePage>();

    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty; // Markdown

    /// <summary>Ordenação entre sub-páginas irmãs (ascendente).</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
