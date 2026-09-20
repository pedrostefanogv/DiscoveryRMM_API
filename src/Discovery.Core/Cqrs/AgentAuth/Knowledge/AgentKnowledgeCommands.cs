using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AgentAuth.Knowledge;

/// <param name="Cursor">Cursor keyset (base64 ticks|guidN) da página anterior; null = primeira página.</param>
/// <param name="Limit">Tamanho da página (clampado a 1–500).</param>
public sealed record GetKnowledgeArticlesQuery(Guid AgentId, string? Category = null, string? Cursor = null, int Limit = 200) : IQuery<Result<object>>;
public sealed record GetKnowledgeArticleQuery(Guid AgentId, Guid ArticleId) : IQuery<Result<object>>;

/// <summary>
/// Retorna a árvore de sub-páginas internas de um artigo (estilo Notion) para o agente.
/// Cada nó contém suas sub-páginas aninhadas (até 3 níveis).
/// </summary>
public sealed record GetKnowledgeArticlePagesQuery(Guid AgentId, Guid ArticleId) : IQuery<Result<IReadOnlyList<ArticlePageTreeNode>>>;