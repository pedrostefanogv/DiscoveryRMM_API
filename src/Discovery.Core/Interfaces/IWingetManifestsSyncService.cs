using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Sincroniza o catálogo Winget a partir de um shallow clone do branch master
/// do microsoft/winget-pkgs (fonte primária; o feed packages.json é fallback).
/// </summary>
public interface IWingetManifestsSyncService
{
    Task<AppCatalogSyncResultDto> SyncFromManifestsAsync(CancellationToken cancellationToken = default);
}
