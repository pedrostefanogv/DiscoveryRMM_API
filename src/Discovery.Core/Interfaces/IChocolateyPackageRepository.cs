using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IChocolateyPackageRepository
{
    Task<ChocolateyPackage?> GetByPackageIdAsync(string packageId);

    Task<(IReadOnlyList<ChocolateyPackage> Items, int TotalCount)> SearchAsync(
        string? search,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task BulkUpsertAsync(
        IReadOnlyList<ChocolateyPackage> packages,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
