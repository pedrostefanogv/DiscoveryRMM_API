using Discovery.Core.Configuration;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Leitura da configuração global de sensores (blob JSON em
/// server_configurations.sensors_settings_json).
/// </summary>
public static class SensorSettingsStore
{
    public static async Task<SensorsSettings> LoadAsync(DiscoveryDbContext db, CancellationToken cancellationToken = default)
    {
        var json = await db.ServerConfigurations.AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => s.SensorsSettingsJson)
            .FirstOrDefaultAsync(cancellationToken);

        return SensorsSettings.Parse(json);
    }
}
