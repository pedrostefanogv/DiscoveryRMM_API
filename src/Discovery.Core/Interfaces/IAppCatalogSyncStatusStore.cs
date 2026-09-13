using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Persiste o resultado da última sincronização de catálogo por tipo de
/// instalação (Winget/Chocolatey). Gravado tanto pelo job automático (Quartz)
/// quanto pelo sync manual (POST /app-store/sync), para que o status exibido
/// no console web reflita também as execuções automáticas e sobreviva a
/// restarts da API (o estado em memória do AppCatalogBackgroundSyncService
/// é volátil e só cobre o caminho manual).
/// </summary>
public interface IAppCatalogSyncStatusStore
{
    Task SaveResultAsync(
        AppInstallationType installationType,
        AppCatalogSyncResultDto result,
        CancellationToken cancellationToken = default);

    Task<AppCatalogSyncResultDto?> GetResultAsync(
        AppInstallationType installationType,
        CancellationToken cancellationToken = default);
}
