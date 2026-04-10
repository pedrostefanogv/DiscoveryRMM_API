using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IWingetPackageRepository
{
    Task<WingetPackage?> GetByPackageIdAsync(string packageId);

    Task<(IReadOnlyList<WingetPackage> Items, int TotalCount)> SearchAsync(
        string? search,
        string? architecture,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task BulkUpsertAsync(
        IReadOnlyList<WingetPackage> packages,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
