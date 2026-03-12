using Meduza.Core.DTOs;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IAppCatalogSyncService
{
    Task<AppCatalogSyncResultDto> SyncCatalogAsync(
        AppInstallationType installationType,
        CancellationToken cancellationToken = default);
}
