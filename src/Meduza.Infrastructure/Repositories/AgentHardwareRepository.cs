using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AgentHardwareRepository : IAgentHardwareRepository
{
    private readonly MeduzaDbContext _db;

    public AgentHardwareRepository(MeduzaDbContext db) => _db = db;

    public async Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId)
    {
                return await _db.AgentHardwareInfos
                        .AsNoTracking()
                        .SingleOrDefaultAsync(info => info.AgentId == agentId);
    }

    public async Task UpsertAsync(AgentHardwareInfo hardware)
    {
        hardware.UpdatedAt = DateTime.UtcNow;
        hardware.CollectedAt = DateTime.UtcNow;

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
            existing.InventorySchemaVersion = hardware.InventorySchemaVersion;
            existing.InventoryCollectedAt = hardware.InventoryCollectedAt;
            existing.CollectedAt = hardware.CollectedAt;
            existing.UpdatedAt = hardware.UpdatedAt;
        }

        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<DiskInfo>> GetDisksAsync(Guid agentId)
    {
        return await _db.DiskInfos
            .AsNoTracking()
            .Where(disk => disk.AgentId == agentId)
            .ToListAsync();
    }

    public async Task ReplaceDiskInfoAsync(Guid agentId, IEnumerable<DiskInfo> disks)
    {
        await _db.DiskInfos
            .Where(disk => disk.AgentId == agentId)
            .ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        foreach (var disk in disks)
        {
            disk.Id = IdGenerator.NewId();
            disk.AgentId = agentId;
            disk.CollectedAt = now;
        }

        _db.DiskInfos.AddRange(disks);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<NetworkAdapterInfo>> GetNetworkAdaptersAsync(Guid agentId)
    {
        return await _db.NetworkAdapterInfos
            .AsNoTracking()
            .Where(adapter => adapter.AgentId == agentId)
            .ToListAsync();
    }

    public async Task ReplaceNetworkAdaptersAsync(Guid agentId, IEnumerable<NetworkAdapterInfo> adapters)
    {
        await _db.NetworkAdapterInfos
            .Where(adapter => adapter.AgentId == agentId)
            .ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        foreach (var adapter in adapters)
        {
            adapter.Id = IdGenerator.NewId();
            adapter.AgentId = agentId;
            adapter.CollectedAt = now;
        }

        _db.NetworkAdapterInfos.AddRange(adapters);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<MemoryModuleInfo>> GetMemoryModulesAsync(Guid agentId)
    {
        return await _db.MemoryModuleInfos
            .AsNoTracking()
            .Where(module => module.AgentId == agentId)
            .ToListAsync();
    }

    public async Task ReplaceMemoryModulesAsync(Guid agentId, IEnumerable<MemoryModuleInfo> modules)
    {
        await _db.MemoryModuleInfos
            .Where(module => module.AgentId == agentId)
            .ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        foreach (var module in modules)
        {
            module.Id = IdGenerator.NewId();
            module.AgentId = agentId;
            module.CollectedAt = now;
        }

        _db.MemoryModuleInfos.AddRange(modules);
        await _db.SaveChangesAsync();
    }
}
