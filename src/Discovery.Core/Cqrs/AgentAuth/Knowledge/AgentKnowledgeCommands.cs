using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AgentAuth.Knowledge;

public sealed record GetKnowledgeArticlesQuery(Guid AgentId, string? Category = null) : IQuery<Result<object>>;
public sealed record GetKnowledgeArticleQuery(Guid AgentId, Guid ArticleId) : IQuery<Result<object>>;

/// <summary>
/// Retorna a árvore de sub-páginas internas de um artigo (estilo Notion) para o agente.
/// Cada nó contém suas sub-páginas aninhadas (até 3 níveis).
/// </summary>
public sealed record GetKnowledgeArticlePagesQuery(Guid AgentId, Guid ArticleId) : IQuery<Result<IReadOnlyList<ArticlePageTreeNode>>>;