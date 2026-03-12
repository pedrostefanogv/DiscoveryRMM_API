using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IAppPackageRepository
{
    Task<AppPackage?> GetByInstallationTypeAndPackageIdAsync(AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<AppPackage> Items, int TotalCount)> SearchAsync(
        AppInstallationType installationType,
        string? search,
        string? architecture,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppPackage>> GetAllByInstallationTypeAsync(
        AppInstallationType installationType,
        CancellationToken cancellationToken = default);

    Task<int> BulkUpsertAsync(
        IReadOnlyList<AppPackage> packages,
        AppInstallationType installationType,
        CancellationToken cancellationToken = default);

    Task<AppPackage> UpsertCustomAsync(AppPackage package, CancellationToken cancellationToken = default);
}
