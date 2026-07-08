using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IChocolateyPackageRepository
{
    Task<ChocolateyPackage?> GetByPackageIdAsync(string packageId);

    Task<(IReadOnlyList<ChocolateyPackage> Items, int TotalCount)> SearchPageAsync(
        string? search,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task BulkUpsertAsync(
        IReadOnlyList<ChocolateyPackage> packages,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
