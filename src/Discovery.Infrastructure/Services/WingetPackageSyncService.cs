using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class WingetPackageSyncService : IWingetPackageSyncService
{
    private readonly WingetFeedClient _feedClient;
    private readonly IWingetPackageRepository _repo;
    private readonly ILogger<WingetPackageSyncService> _logger;

    private static readonly SemaphoreSlim _syncSemaphore = new(1, 1);

    public WingetPackageSyncService(
        WingetFeedClient feedClient,
        IWingetPackageRepository repo,
        ILogger<WingetPackageSyncService> logger)
    {
        _feedClient = feedClient;
        _repo = repo;
        _logger = logger;
    }

    public async Task<WingetSyncResultDto> SyncCatalogAsync(CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (!_syncSemaphore.Wait(TimeSpan.Zero))
        {
            stopwatch.Stop();
            _logger.LogWarning("Winget sync request rejected: sync already in progress.");
            return new WingetSyncResultDto
            {
                Success = false,
                PackagesUpserted = 0,
                SyncedAt = started,
                Duration = stopwatch.Elapsed,
                Error = "A sync operation is already in progress. Please try again later."
            };
        }

        try
        {
            var snapshot = await _feedClient.FetchLatestAsync(cancellationToken);
            await _repo.BulkUpsertAsync(snapshot.Packages, cancellationToken);

            stopwatch.Stop();
            _logger.LogInformation(
                "Winget catalog sync complete: {Upserted} packages, duration {Duration}.",
                snapshot.Packages.Count,
                stopwatch.Elapsed);

            return new WingetSyncResultDto
            {
                Success = true,
                PackagesUpserted = snapshot.Packages.Count,
                SyncedAt = started,
                SourceGeneratedAt = snapshot.GeneratedAt,
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning("Winget sync was cancelled after {Duration}.", stopwatch.Elapsed);
            return new WingetSyncResultDto
            {
                Success = false,
                PackagesUpserted = 0,
                SyncedAt = started,
                Duration = stopwatch.Elapsed,
                Error = "Sync was cancelled."
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Winget sync failed after {Duration}.", stopwatch.Elapsed);
            return new WingetSyncResultDto
            {
                Success = false,
                PackagesUpserted = 0,
                SyncedAt = started,
                Duration = stopwatch.Elapsed,
                Error = ex.Message
            };
        }
        finally
        {
            _syncSemaphore.Release();
        }
    }
}
