using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Search.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Search.QueryHandlers;

public sealed class UniversalSearchQueryHandler(ISearchService svc)
    : IRequestHandler<UniversalSearchQuery, Result<SearchResultDto>>
{
    public async Task<Result<SearchResultDto>> Handle(UniversalSearchQuery q, CancellationToken ct)
    {
        var r = await svc.SearchAsync(q.UserId, q.Query, q.MaxResults, ct);
        var groups = r.Groups.Select(g => new SearchGroupDto(
            g.EntityType, g.Label, g.Icon,
            g.Items.Select(i => new SearchHitDto(i.Id, i.Title, i.Subtitle, i.Description, i.EntityType, i.ClientId, i.ClientName, i.SiteId, i.SiteName, i.Url)).ToList() as IReadOnlyList<SearchHitDto>
        )).ToList() as IReadOnlyList<SearchGroupDto>;
        return Result<SearchResultDto>.Success(new SearchResultDto(groups, r.TotalResults));
    }
}
