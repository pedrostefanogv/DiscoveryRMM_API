using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Search.Queries;

public sealed record UniversalSearchQuery(Guid UserId, string Query, int MaxResults = 10) : IQuery<Result<SearchResultDto>>;

public sealed record SearchResultDto(IReadOnlyList<SearchGroupDto> Groups, int TotalResults);
public sealed record SearchGroupDto(string EntityType, string Label, string Icon, IReadOnlyList<SearchHitDto> Items);
public sealed record SearchHitDto(Guid Id, string Title, string? Subtitle, string? Description, string EntityType, Guid? ClientId, string? ClientName, Guid? SiteId, string? SiteName, string? Url);
