using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Knowledge.Queries;

public sealed record SearchKnowledgeQuery(string Query, Guid? ClientId, Guid? SiteId, int MaxResults = 10) : IQuery<Result<IReadOnlyList<ArticleResponse>>>;
public sealed record ListKnowledgeArticlesQuery(Guid? ClientId, Guid? SiteId, string? Cursor = null, int Limit = 50) : IQuery<Result<IReadOnlyList<ArticleResponse>>>;
public sealed record ListKnowledgeArticlesByUserScopeQuery(string? Cursor = null, int Limit = 20, string? Status = null, Guid? DepartmentId = null, string? Category = null) : IQuery<Result<CursorPageDto<ArticleListItem>>>;
public sealed record GetKnowledgeArticleByIdQuery(Guid Id) : IQuery<Result<ArticleResponse>>;
