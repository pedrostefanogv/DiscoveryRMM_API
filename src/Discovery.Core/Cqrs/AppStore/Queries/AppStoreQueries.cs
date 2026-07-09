using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AppStore.Queries;

public sealed record SearchAppStoreQuery(string? Search, string? Architecture, string? Cursor, int Limit = 20) : IQuery<Result<IReadOnlyList<AppStoreDto>>>;
public sealed record GetAppStoreCatalogQuery(int InstallationType, string? Search, string? Architecture, string? Cursor, int Limit = 20) : IQuery<Result<IReadOnlyList<AppStoreDto>>>;
public sealed record GetAppStoreEffectiveAppsQuery(Guid? ClientId, Guid? SiteId, Guid? AgentId) : IQuery<Result<IReadOnlyList<AppStoreDto>>>;
public sealed record AppStoreDto(string PackageId, string Name, string Version, string Publisher, string AppType);