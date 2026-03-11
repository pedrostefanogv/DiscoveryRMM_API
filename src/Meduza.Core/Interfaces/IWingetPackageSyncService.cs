using Meduza.Core.DTOs;

namespace Meduza.Core.Interfaces;

public interface IWingetPackageSyncService
{
    Task<WingetSyncResultDto> SyncCatalogAsync(CancellationToken cancellationToken = default);
}
