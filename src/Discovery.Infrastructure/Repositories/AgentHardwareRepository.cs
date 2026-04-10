using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Discovery.Infrastructure.Repositories;

public class AgentHardwareRepository : IAgentHardwareRepository
{
    private readonly DiscoveryDbContext _db;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public AgentHardwareRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId)
    {
        return await _db.AgentHardwareInfos
            .AsNoTracking()
            .SingleOrDefaultAsync(info => info.AgentId == agentId);
    }

    public async Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId)
    {
        var json = await _db.AgentHardwareInfos
            .AsNoTracking()
            .Where(info => info.AgentId == agentId)
            .Select(info => info.HardwareComponentsJson)
            .SingleOrDefaultAsync();

        return DeserializeComponents(json) ?? new AgentHardwareComponents();
    }

    public async Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null)
    {
        var now = DateTime.UtcNow;
        hardware.UpdatedAt = now;
        hardware.CollectedAt = now;
        hardware.TotalDisksCount = components?.Disks.Count;

        if (components is not null)
            hardware.HardwareComponentsJson = JsonSerializer.Serialize(components, JsonOptions);

        var existing = await _db.AgentHardwareInfos
            .SingleOrDefaultAsync(info => info.AgentId == hardware.AgentId);

        if (existing is null)
        {
            hardware.Id = IdGenerator.NewId();
            _db.AgentHardwareInfos.Add(hardware);
        }
        else
        {
            existing.Manufacturer = hardware.Manufacturer;
            existing.Model = hardware.Model;
            existing.SerialNumber = hardware.SerialNumber;
            existing.MotherboardManufacturer = hardware.MotherboardManufacturer;
            existing.MotherboardModel = hardware.MotherboardModel;
            existing.MotherboardSerialNumber = hardware.MotherboardSerialNumber;
            existing.Processor = hardware.Processor;
            existing.ProcessorCores = hardware.ProcessorCores;
            existing.ProcessorThreads = hardware.ProcessorThreads;
            existing.ProcessorArchitecture = hardware.ProcessorArchitecture;
            existing.TotalMemoryBytes = hardware.TotalMemoryBytes;
            existing.BiosVersion = hardware.BiosVersion;
            existing.BiosManufacturer = hardware.BiosManufacturer;
            existing.OsName = hardware.OsName;
            existing.OsVersion = hardware.OsVersion;
            existing.OsBuild = hardware.OsBuild;
            existing.OsArchitecture = hardware.OsArchitecture;
            existing.InventoryRaw = hardware.InventoryRaw;
            existing.HardwareComponentsJson = hardware.HardwareComponentsJson;
            existing.InventorySchemaVersion = hardware.InventorySchemaVersion;
            existing.InventoryCollectedAt = hardware.InventoryCollectedAt;
            existing.CollectedAt = hardware.CollectedAt;
            existing.UpdatedAt = hardware.UpdatedAt;
            existing.TotalDisksCount = hardware.TotalDisksCount;
        }

        await _db.SaveChangesAsync();
    }

    private static AgentHardwareComponents? DeserializeComponents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentHardwareComponents>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
