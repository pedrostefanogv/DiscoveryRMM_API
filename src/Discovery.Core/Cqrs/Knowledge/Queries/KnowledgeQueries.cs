using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Knowledge.Queries;

public sealed record SearchKnowledgeQuery(string Query, Guid? ClientId, Guid? SiteId, int MaxResults = 10) : IQuery<Result<IReadOnlyList<KnowledgeArticleDto>>>;
public sealed record ListKnowledgeArticlesQuery(Guid? ClientId, Guid? SiteId, string? Cursor = null, int Limit = 50) : IQuery<Result<IReadOnlyList<KnowledgeArticleDto>>>;
public sealed record GetKnowledgeArticleByIdQuery(Guid Id) : IQuery<Result<KnowledgeArticleDto>>;

public sealed record KnowledgeArticleDto(Guid Id, string Title, string? Summary, string? Category, DateTime CreatedAt, DateTime UpdatedAt);
