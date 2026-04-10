using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

public interface IChocolateyPackageSyncService
{
    Task<ChocolateySyncResultDto> SyncCatalogAsync(CancellationToken cancellationToken = default);
}
