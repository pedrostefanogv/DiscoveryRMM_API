using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Knowledge;

public sealed record GetKnowledgeArticlesQuery(Guid AgentId, string? Category = null) : IQuery<Result<object>>;
public sealed record GetKnowledgeArticleQuery(Guid AgentId, Guid ArticleId) : IQuery<Result<object>>;