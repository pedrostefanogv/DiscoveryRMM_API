using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Persiste o resultado da última sincronização de catálogo por tipo no
/// ServerConfiguration (coluna app_catalog_sync_settings_json). Falha de
/// persistência de status NUNCA derruba o sync em si — é apenas telemetria.
/// </summary>
public class AppCatalogSyncStatusStore : IAppCatalogSyncStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServerConfigurationRepository _serverRepo;
    private readonly ILogger<AppCatalogSyncStatusStore> _logger;

    public AppCatalogSyncStatusStore(
        IServerConfigurationRepository serverRepo,
        ILogger<AppCatalogSyncStatusStore> logger)
    {
        _serverRepo = serverRepo;
        _logger = logger;
    }

    public async Task SaveResultAsync(
        AppInstallationType installationType,
        AppCatalogSyncResultDto result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _serverRepo.GetOrCreateDefaultAsync();
            var state = Parse(config.AppCatalogSyncSettingsJson);
            state[installationType.ToString()] = result;

            config.AppCatalogSyncSettingsJson = JsonSerializer.Serialize(state, JsonOptions);
            config.UpdatedAt = DateTime.UtcNow;
            await _serverRepo.UpdateAsync(config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao persistir status de sync do catálogo ({Type}); o sync em si não é afetado.",
                installationType);
        }
    }

    public async Task<AppCatalogSyncResultDto?> GetResultAsync(
        AppInstallationType installationType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _serverRepo.GetAsync();
            if (config is null)
                return null;

            var state = Parse(config.AppCatalogSyncSettingsJson);
            return state.TryGetValue(installationType.ToString(), out var dto) ? dto : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao ler status de sync do catálogo ({Type}); retornando sem resultado persistido.",
                installationType);
            return null;
        }
    }

    private static Dictionary<string, AppCatalogSyncResultDto> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new Dictionary<string, AppCatalogSyncResultDto>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, AppCatalogSyncResultDto>>(json, JsonOptions)
                   ?? new Dictionary<string, AppCatalogSyncResultDto>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, AppCatalogSyncResultDto>();
        }
    }
}
