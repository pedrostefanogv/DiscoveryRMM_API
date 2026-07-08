using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Knowledge.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Knowledge;

public sealed class SearchKnowledgeQueryHandler : IRequestHandler<SearchKnowledgeQuery, Result<IReadOnlyList<KnowledgeArticleDto>>>
{ public Task<Result<IReadOnlyList<KnowledgeArticleDto>>> Handle(SearchKnowledgeQuery q, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<KnowledgeArticleDto>>.Success(Array.Empty<KnowledgeArticleDto>())); }

public sealed class ListKnowledgeArticlesQueryHandler : IRequestHandler<ListKnowledgeArticlesQuery, Result<IReadOnlyList<KnowledgeArticleDto>>>
{ public Task<Result<IReadOnlyList<KnowledgeArticleDto>>> Handle(ListKnowledgeArticlesQuery q, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<KnowledgeArticleDto>>.Success(Array.Empty<KnowledgeArticleDto>())); }

public sealed class GetKnowledgeArticleByIdQueryHandler : IRequestHandler<GetKnowledgeArticleByIdQuery, Result<KnowledgeArticleDto>>
{ public Task<Result<KnowledgeArticleDto>> Handle(GetKnowledgeArticleByIdQuery q, CancellationToken ct) => Task.FromResult(Result<KnowledgeArticleDto>.Failure(Error.NotFound($"Article {q.Id} not found"))); }
