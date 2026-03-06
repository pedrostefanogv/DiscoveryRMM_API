using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class ServerConfigurationRepository : IServerConfigurationRepository
{
    private readonly MeduzaDbContext _db;

    public ServerConfigurationRepository(MeduzaDbContext db) => _db = db;

    public async Task<ServerConfiguration?> GetAsync()
    {
        return await _db.ServerConfigurations
            .AsNoTracking()
            .OrderBy(config => config.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<ServerConfiguration> GetOrCreateDefaultAsync()
    {
        var existing = await GetAsync();
        if (existing is not null) return existing;

        var config = new ServerConfiguration { Id = IdGenerator.NewId() };
        _db.ServerConfigurations.Add(config);
        await _db.SaveChangesAsync();
        return config;
    }

    public async Task UpdateAsync(ServerConfiguration config)
    {
        config.UpdatedAt = DateTime.UtcNow;
        _db.ServerConfigurations.Update(config);
        await _db.SaveChangesAsync();
    }

    public async Task<bool> ExistsAsync()
    {
        return await _db.ServerConfigurations.AsNoTracking().AnyAsync();
    }
}
