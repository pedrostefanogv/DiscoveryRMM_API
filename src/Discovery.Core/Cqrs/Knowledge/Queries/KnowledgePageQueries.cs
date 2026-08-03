using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Knowledge.Queries;

// ─── Sub-páginas internas do artigo (estilo Notion) ────────────────

/// <summary>
/// Retorna a árvore de sub-páginas internas de um artigo (estilo Notion).
/// Cada nó contém suas sub-páginas aninhadas (até 3 níveis).
/// </summary>
public sealed record GetArticlePagesQuery(Guid ArticleId) : IQuery<Result<IReadOnlyList<ArticlePageTreeNode>>>;

/// <summary>Obtém uma sub-página interna específica de um artigo.</summary>
public sealed record GetArticlePageQuery(Guid ArticleId, Guid PageId) : IQuery<Result<ArticlePageResponse>>;
