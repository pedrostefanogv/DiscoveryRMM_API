using System.Text.Json;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class ServerConfigurationRepository : IServerConfigurationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string CacheKey = "config:server:singleton";
    private const int CacheTtlSeconds = 300;

    private readonly MeduzaDbContext _db;
    private readonly IRedisService _redisService;

    public ServerConfigurationRepository(MeduzaDbContext db, IRedisService redisService)
    {
        _db = db;
        _redisService = redisService;
    }

    public async Task<ServerConfiguration?> GetAsync()
    {
        var cached = await _redisService.GetAsync(CacheKey);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            try
            {
                var deserialized = JsonSerializer.Deserialize<ServerConfiguration>(cached, JsonOptions);
                if (deserialized is not null)
                    return deserialized;
            }
            catch (JsonException)
            {
                await _redisService.DeleteAsync(CacheKey);
            }
        }

        var config = await _db.ServerConfigurations
            .AsNoTracking()
            .OrderBy(config => config.CreatedAt)
            .FirstOrDefaultAsync();

        if (config is not null)
            await CacheAsync(config);

        return config;
    }

    public async Task<ServerConfiguration> GetOrCreateDefaultAsync()
    {
        var existing = await GetAsync();
        if (existing is not null) return existing;

        var config = new ServerConfiguration { Id = IdGenerator.NewId() };
        _db.ServerConfigurations.Add(config);
        await _db.SaveChangesAsync();
        await CacheAsync(config);
        return config;
    }

    public async Task UpdateAsync(ServerConfiguration config)
    {
        config.UpdatedAt = DateTime.UtcNow;
        _db.ServerConfigurations.Update(config);
        await _db.SaveChangesAsync();
        await CacheAsync(config);
    }

    public async Task<bool> ExistsAsync()
    {
        return await _db.ServerConfigurations.AsNoTracking().AnyAsync();
    }

    private async Task CacheAsync(ServerConfiguration config)
    {
        var payload = JsonSerializer.Serialize(config, JsonOptions);
        await _redisService.SetAsync(CacheKey, payload, CacheTtlSeconds);
    }
}
