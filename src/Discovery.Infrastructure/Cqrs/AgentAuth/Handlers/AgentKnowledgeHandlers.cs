using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Knowledge;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetKnowledgeArticlesHandler() : IRequestHandler<GetKnowledgeArticlesQuery, Result<object>>
{ public Task<Result<object>> Handle(GetKnowledgeArticlesQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class GetKnowledgeArticleHandler() : IRequestHandler<GetKnowledgeArticleQuery, Result<object>>
{ public Task<Result<object>> Handle(GetKnowledgeArticleQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }