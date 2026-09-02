using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AppCatalogSyncService : IAppCatalogSyncService
{
    private readonly WingetFeedClient _wingetFeedClient;
    private readonly ChocolateyApiClient _chocolateyApiClient;
    private readonly IAppPackageRepository _appPackageRepository;
    private readonly IWingetManifestsSyncService? _manifestsSyncService;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<AppCatalogSyncService> _logger;

    public AppCatalogSyncService(
        WingetFeedClient wingetFeedClient,
        ChocolateyApiClient chocolateyApiClient,
        IAppPackageRepository appPackageRepository,
        ILogger<AppCatalogSyncService> logger,
        IWingetManifestsSyncService? manifestsSyncService = null,
        IConfiguration? configuration = null)
    {
        _wingetFeedClient = wingetFeedClient;
        _chocolateyApiClient = chocolateyApiClient;
        _appPackageRepository = appPackageRepository;
        _manifestsSyncService = manifestsSyncService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AppCatalogSyncResultDto> SyncCatalogAsync(
        AppInstallationType installationType,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (installationType == AppInstallationType.Winget)
        {
            // Fonte primária = manifests do winget-pkgs (rev. 3); feed packages.json = fallback.
            var source = _configuration?.GetValue<string?>("AppCatalog:Winget:Source") ?? "manifests";
            var feedEnabled = source.Equals("feed", StringComparison.OrdinalIgnoreCase)
                              || source.Equals("both", StringComparison.OrdinalIgnoreCase);

            if (_manifestsSyncService is not null
                && (source.Equals("manifests", StringComparison.OrdinalIgnoreCase) || source.Equals("both", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var manifestsResult = await _manifestsSyncService.SyncFromManifestsAsync(cancellationToken);
                    if (!feedEnabled)
                        return manifestsResult;

                    // "both": se o manifests falhou, cai para o feed; se sucesso, sincroniza o feed com anti-downgrade.
                    if (!manifestsResult.Success)
                        _logger.LogWarning("Manifests sync falhou ({Error}); caindo para o feed.", manifestsResult.Error);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!feedEnabled)
                    {
                        _logger.LogError(ex, "Winget manifests sync falhou.");
                        return new AppCatalogSyncResultDto
                        {
                            InstallationType = installationType,
                            Success = false,
                            PackagesUpserted = 0,
                            PagesProcessed = 0,
                            SyncedAt = startedAt,
                            Duration = stopwatch.Elapsed,
                            Error = ex.Message
                        };
                    }
                    _logger.LogWarning(ex, "Winget manifests sync falhou; caindo para o feed.");
                }
            }

            if (!feedEnabled)
            {
                return new AppCatalogSyncResultDto
                {
                    InstallationType = installationType,
                    Success = false,
                    PackagesUpserted = 0,
                    PagesProcessed = 0,
                    SyncedAt = startedAt,
                    Duration = stopwatch.Elapsed,
                    Error = "Winget source configurada como 'manifests' mas o serviço de manifests não está disponível."
                };
            }

            try
            {
                var snapshot = await _wingetFeedClient.FetchLatestAsync(cancellationToken);
                var mappedPackages = snapshot.Packages
                    .Select(p => new AppPackage
                    {
                        InstallationType = AppInstallationType.Winget,
                        PackageId = p.PackageId,
                        Name = p.Name,
                        Publisher = p.Publisher,
                        Version = p.Version,
                        Description = p.Description,
                        IconUrl = p.Icon,
                        SiteUrl = p.Homepage,
                        InstallCommand = p.InstallCommand,
                        MetadataJson = JsonSerializer.Serialize(new
                        {
                            license = p.License,
                            category = p.Category,
                            tags = p.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                            installerUrlsByArch = SafeParseInstallerUrls(p.InstallerUrlsJson),
                            silent = p.SilentCommand,
                            silentWithProgress = p.SilentWithProgressCommand
                        }),
                        SourceGeneratedAt = p.SourceGeneratedAt,
                        LastUpdated = p.LastUpdated
                    })
                    .ToList();

                var upserted = await _appPackageRepository.BulkUpsertAsync(
                    mappedPackages, AppInstallationType.Winget, cancellationToken,
                    preventDowngrade: feedEnabled && _manifestsSyncService is not null);
                stopwatch.Stop();

                return new AppCatalogSyncResultDto
                {
                    InstallationType = installationType,
                    Success = true,
                    PackagesUpserted = upserted,
                    PagesProcessed = 1,
                    SyncedAt = startedAt,
                    SourceGeneratedAt = snapshot.GeneratedAt,
                    Duration = stopwatch.Elapsed
                };
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return new AppCatalogSyncResultDto
                {
                    InstallationType = installationType,
                    Success = false,
                    PackagesUpserted = 0,
                    PagesProcessed = 0,
                    SyncedAt = startedAt,
                    Duration = stopwatch.Elapsed,
                    Error = "Sync was cancelled."
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Winget unified sync failed.");
                return new AppCatalogSyncResultDto
                {
                    InstallationType = installationType,
                    Success = false,
                    PackagesUpserted = 0,
                    PagesProcessed = 0,
                    SyncedAt = startedAt,
                    Duration = stopwatch.Elapsed,
                    Error = ex.Message
                };
            }
        }

        if (installationType == AppInstallationType.Chocolatey)
        {
            var pagesProcessed = 0;
            var upserted = 0;
            try
            {
                await foreach (var page in _chocolateyApiClient.FetchAllPackagesAsync(cancellationToken: cancellationToken))
                {
                    pagesProcessed++;
                    var mappedPackages = page.Packages
                        .Select(p => new AppPackage
                        {
                            InstallationType = AppInstallationType.Chocolatey,
                            PackageId = p.PackageId,
                            Name = p.Name,
                            Publisher = p.Publisher,
                            Version = p.Version,
                            Description = p.Description,
                            IconUrl = null,
                            SiteUrl = p.Homepage,
                            InstallCommand = $"choco install {p.PackageId}",
                            MetadataJson = JsonSerializer.Serialize(new
                            {
                                licenseUrl = p.LicenseUrl,
                                tags = p.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                                downloadCount = p.DownloadCount
                            }),
                            SourceGeneratedAt = null,
                            LastUpdated = p.LastUpdated
                        })
                        .ToList();

                    upserted += await _appPackageRepository.BulkUpsertAsync(
                        mappedPackages,
                        AppInstallationType.Chocolatey,
                        cancellationToken);
                }

                stopwatch.Stop();
                return new AppCatalogSyncResultDto
                {
                    InstallationType = installationType,
                    Success = true,
                    PackagesUpserted = upserted,
                    PagesProcessed = pagesProcessed,
                    SyncedAt = startedAt,
                    SourceGeneratedAt = null,
                    Duration = stopwatch.Elapsed
                };
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return new AppCatalogSyncResultDto
                {
                    InstallationType = installationType,
                    Success = false,
                    PackagesUpserted = upserted,
                    PagesProcessed = pagesProcessed,
                    SyncedAt = startedAt,
                    Duration = stopwatch.Elapsed,
                    Error = "Sync was cancelled."
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Chocolatey unified sync failed.");
                return new AppCatalogSyncResultDto
                {
                    InstallationType = installationType,
                    Success = false,
                    PackagesUpserted = upserted,
                    PagesProcessed = pagesProcessed,
                    SyncedAt = startedAt,
                    Duration = stopwatch.Elapsed,
                    Error = ex.Message
                };
            }
        }

        return new AppCatalogSyncResultDto
        {
            InstallationType = installationType,
            Success = false,
            PackagesUpserted = 0,
            PagesProcessed = 0,
            SyncedAt = DateTime.UtcNow,
            Duration = TimeSpan.Zero,
            Error = "Sync for custom apps is not supported. Custom apps are managed manually."
        };
    }

    private static IReadOnlyDictionary<string, string> SafeParseInstallerUrls(string installerUrlsJson)
    {
        if (string.IsNullOrWhiteSpace(installerUrlsJson))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(installerUrlsJson);
            return parsed ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
