using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAppCatalogSyncService
{
    Task<AppCatalogSyncResultDto> SyncCatalogAsync(
        AppInstallationType installationType,
        CancellationToken cancellationToken = default);
}
