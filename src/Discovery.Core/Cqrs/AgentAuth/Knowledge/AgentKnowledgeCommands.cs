using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Knowledge;

public sealed record GetKnowledgeArticlesQuery : IQuery<Result<object>>;
public sealed record GetKnowledgeArticleQuery(Guid ArticleId) : IQuery<Result<object>>;