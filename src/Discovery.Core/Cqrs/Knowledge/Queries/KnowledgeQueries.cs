using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Knowledge.Queries;

public sealed record SearchKnowledgeQuery(string Query, Guid? ClientId, Guid? SiteId, int MaxResults = 10) : IQuery<Result<IReadOnlyList<ArticleResponse>>>;

[Obsolete("Use ListKnowledgeArticlesByUserScopeQuery instead. Kept for backward compatibility only.")]
public sealed record ListKnowledgeArticlesQuery(Guid? ClientId, Guid? SiteId, string? Cursor = null, int Limit = 50) : IQuery<Result<IReadOnlyList<ArticleResponse>>>;

/// <summary>
/// Lista artigos com base na ACL do usuário (multi-escopo).
/// Quando clientId/siteId são informados, refinam o escopo (precisa ter acesso).
/// Sem filtros, retorna todos os artigos visíveis ao usuário.
/// </summary>
public sealed record ListKnowledgeArticlesByUserScopeQuery(
    string? Cursor = null,
    int Limit = 20,
    string? Status = null,
    Guid? DepartmentId = null,
    string? Category = null,
    Guid? ClientId = null,
    Guid? SiteId = null) : IQuery<Result<CursorPageDto<ArticleListItem>>>;

public sealed record GetKnowledgeArticleByIdQuery(Guid Id) : IQuery<Result<ArticleResponse>>;
