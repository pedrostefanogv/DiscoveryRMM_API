using Meduza.Core.DTOs;

namespace Meduza.Core.Interfaces;

public interface IChocolateyPackageSyncService
{
    Task<ChocolateySyncResultDto> SyncCatalogAsync(CancellationToken cancellationToken = default);
}
