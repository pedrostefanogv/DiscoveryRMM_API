using Discovery.Core.Catalog;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Patches;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Ingestão periódica de manifestos do winget-pkgs para o catálogo (WU-21/D24).
///
/// Para cada app de terceiros que aparece no inventário da frota, busca a versão
/// mais recente no repositório público, baixa o `@id`.installer.yaml`, parseia
/// e grava como <c>pending</c> — curadoria humana decide o que vira <c>active</c>.
///
/// Por que isso existe: sem ingestão, alguém teria de colar YAML na API para cada
/// versão nova. O job automatiza o "onde estão os metadados", não a decisão de
/// confiar no pacote (essa continua humana, D24).
///
/// Rede: uma requisição de listagem + uma de manifesto por app, no máximo
/// <see cref="MaxAppsPerRun"/> por execução, com falha isolada por app.
/// </summary>
[DisallowConcurrentExecution]
public sealed class AppCatalogIngestionJob : IJob
{
    private const int MaxAppsPerRun = 100;

    public static readonly JobKey Key = new("app-catalog-ingestion", "patches");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<AppCatalogIngestionJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var patchRepo = scope.ServiceProvider.GetRequiredService<IPatchRepository>();
        var catalogRepo = scope.ServiceProvider.GetRequiredService<IAppCatalogRepository>();
        var source = scope.ServiceProvider.GetRequiredService<WingetManifestSource>();

        try
        {
            var keys = await patchRepo.GetThirdPartyAppKeysAsync(MaxAppsPerRun, ct);
            if (keys.Count == 0)
                return;

            var ingested = 0;
            var skipped = 0;

            foreach (var key in keys)
            {
                if (ct.IsCancellationRequested)
                    break;

                var separator = key.IndexOf(':');
                if (separator <= 0 || separator + 1 >= key.Length)
                    continue;

                var appSource = key[..separator].Trim().ToLowerInvariant();
                var appId = key[(separator + 1)..].Trim();
                if (appSource != "winget" || appId.Length == 0)
                {
                    // Chocolatey tem feed próprio (não está no winget-pkgs); fica
                    // para uma ingestão dedicada.
                    skipped++;
                    continue;
                }

                try
                {
                    var version = await source.GetLatestVersionAsync(appId, ct);
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        skipped++;
                        continue;
                    }

                    var yaml = await source.GetInstallerManifestAsync(appId, version!, ct);
                    if (string.IsNullOrWhiteSpace(yaml))
                    {
                        skipped++;
                        continue;
                    }

                    var (manifest, error) = Discovery.Core.Catalog.WingetManifestParser.Parse(yaml);
                    if (manifest is null)
                    {
                        logger.LogWarning(
                            "Catalogo: manifesto invalido para {AppId} {Version}: {Error}", appId, version, error);
                        skipped++;
                        continue;
                    }

                    await catalogRepo.UpsertAsync(new AppCatalogEntry
                    {
                        Source = "winget",
                        PackageId = manifest.PackageIdentifier,
                        Name = string.IsNullOrWhiteSpace(manifest.PackageName)
                            ? manifest.PackageIdentifier
                            : manifest.PackageName!,
                        Publisher = manifest.Publisher,
                        Version = manifest.PackageVersion,
                        InstallerUrl = manifest.Installer.InstallerUrl,
                        Sha256 = manifest.Installer.Sha256,
                        InstallerType = manifest.Installer.InstallerType,
                        SilentArgs = manifest.Installer.SilentArgs,
                        ProductCode = manifest.Installer.ProductCode,
                        ReleaseDate = manifest.ReleaseDate,
                        CuratedBy = "ingestao-automatica",
                        Status = "pending"
                    }, ct);

                    ingested++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Falha de um app não derruba a ingestão dos demais.
                    logger.LogWarning(ex, "Catalogo: falha ao ingerir {Key}", key);
                    skipped++;
                }
            }

            if (ingested > 0 || skipped > 0)
                logger.LogInformation(
                    "Catalogo: ingestao winget-pkgs concluida (ingeridos={Ingested}, ignorados={Skipped}).",
                    ingested, skipped);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Catalogo: ingestao winget-pkgs falhou.");
        }
    }
}
