using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class ChocolateyPackageSyncService : IChocolateyPackageSyncService
{
    private readonly ChocolateyApiClient _apiClient;
    private readonly IChocolateyPackageRepository _repo;
    private readonly ILogger<ChocolateyPackageSyncService> _logger;
    
    // Semaphore to prevent concurrent sync executions
    private static readonly SemaphoreSlim _syncSemaphore = new(1, 1);
    private static string? _resumeFromUrl;

    public ChocolateyPackageSyncService(
        ChocolateyApiClient apiClient,
        IChocolateyPackageRepository repo,
        ILogger<ChocolateyPackageSyncService> logger)
    {
        _apiClient = apiClient;
        _repo = repo;
        _logger = logger;
    }

    public async Task<ChocolateySyncResultDto> SyncCatalogAsync(CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        int totalUpserted = 0;
        int pagesProcessed = 0;

        // Attempt non-blocking acquisition of the semaphore
        if (!_syncSemaphore.Wait(TimeSpan.Zero))
        {
            stopwatch.Stop();
            _logger.LogWarning("Chocolatey sync request rejected: sync already in progress.");
            return new ChocolateySyncResultDto
            {
                Success = false,
                PackagesUpserted = 0,
                PagesProcessed = 0,
                SyncedAt = started,
                Duration = stopwatch.Elapsed,
                Error = "A sync operation is already in progress. Please try again later."
            };
        }

        try
        {
            var startUrl = _resumeFromUrl;
            _logger.LogInformation("Chocolatey catalog sync started. Resume mode: {ResumeMode}",
                string.IsNullOrWhiteSpace(startUrl) ? "from-beginning" : "from-last-cursor");

            await foreach (var pageResult in _apiClient.FetchAllPackagesAsync(startUrl, cancellationToken))
            {
                var page = pageResult.Packages;
                if (page.Count == 0)
                    break;

                await _repo.BulkUpsertAsync(page, cancellationToken);
                totalUpserted += page.Count;
                pagesProcessed++;
                _resumeFromUrl = pageResult.NextPageUrl;

                _logger.LogDebug(
                    "Chocolatey sync: page {Page} upserted {Count} packages (total so far: {Total}).",
                    pagesProcessed, page.Count, totalUpserted);
            }

            // Full catalog completed, clear resume cursor
            _resumeFromUrl = null;

            stopwatch.Stop();
            _logger.LogInformation(
                "Chocolatey catalog sync complete: {Upserted} packages in {Pages} pages, duration {Duration}.",
                totalUpserted, pagesProcessed, stopwatch.Elapsed);

            return new ChocolateySyncResultDto
            {
                Success = true,
                PackagesUpserted = totalUpserted,
                PagesProcessed = pagesProcessed,
                SyncedAt = started,
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning("Chocolatey sync was cancelled after {Duration}. Next run will resume from last successful cursor.", stopwatch.Elapsed);
            return new ChocolateySyncResultDto
            {
                Success = false,
                PackagesUpserted = totalUpserted,
                PagesProcessed = pagesProcessed,
                SyncedAt = started,
                Duration = stopwatch.Elapsed,
                Error = "Sync was cancelled. Next run will resume from the last successful page."
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Chocolatey sync failed after {Duration}. Next run will resume from last successful cursor.", stopwatch.Elapsed);
            return new ChocolateySyncResultDto
            {
                Success = false,
                PackagesUpserted = totalUpserted,
                PagesProcessed = pagesProcessed,
                SyncedAt = started,
                Duration = stopwatch.Elapsed,
                Error = $"{ex.Message} Next run will resume from the last successful page."
            };
        }
        finally
        {
            // Always release the semaphore when done
            _syncSemaphore.Release();
        }
    }
}
