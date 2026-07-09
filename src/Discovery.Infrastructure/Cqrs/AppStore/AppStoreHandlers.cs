using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AppStore.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AppStore;

public sealed class SearchAppStoreQueryHandler : IRequestHandler<SearchAppStoreQuery, Result<IReadOnlyList<AppStoreDto>>>
{ public Task<Result<IReadOnlyList<AppStoreDto>>> Handle(SearchAppStoreQuery q, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<AppStoreDto>>.Success(Array.Empty<AppStoreDto>())); }

public sealed class GetAppStoreEffectiveAppsQueryHandler : IRequestHandler<GetAppStoreEffectiveAppsQuery, Result<IReadOnlyList<AppStoreDto>>>
{ public Task<Result<IReadOnlyList<AppStoreDto>>> Handle(GetAppStoreEffectiveAppsQuery q, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<AppStoreDto>>.Success(Array.Empty<AppStoreDto>())); }

public sealed class GetAppStoreCatalogQueryHandler : IRequestHandler<GetAppStoreCatalogQuery, Result<IReadOnlyList<AppStoreDto>>>
{ public Task<Result<IReadOnlyList<AppStoreDto>>> Handle(GetAppStoreCatalogQuery q, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<AppStoreDto>>.Success(Array.Empty<AppStoreDto>())); }