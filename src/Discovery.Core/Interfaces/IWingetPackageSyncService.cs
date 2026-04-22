using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

public interface IWingetPackageSyncService
{
    Task<WingetSyncResultDto> SyncCatalogAsync(CancellationToken cancellationToken = default);
}
