using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class AgentHardwareRepository : IAgentHardwareRepository
{
    private readonly IDbConnectionFactory _db;

    public AgentHardwareRepository(IDbConnectionFactory db) => _db = db;

    public async Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<AgentHardwareInfo>(
            """
            SELECT id, agent_id AS AgentId, manufacturer, model, serial_number AS SerialNumber,
                   motherboard_manufacturer AS MotherboardManufacturer, motherboard_model AS MotherboardModel,
                   motherboard_serial_number AS MotherboardSerialNumber,
                   processor, processor_cores AS ProcessorCores, processor_threads AS ProcessorThreads,
                   processor_architecture AS ProcessorArchitecture,
                   total_memory_bytes AS TotalMemoryBytes,
                   bios_version AS BiosVersion, bios_manufacturer AS BiosManufacturer,
                   os_name AS OsName, os_version AS OsVersion, os_build AS OsBuild,
                   os_architecture AS OsArchitecture,
                   collected_at AS CollectedAt, updated_at AS UpdatedAt
            FROM agent_hardware_info WHERE agent_id = @AgentId
            """, new { AgentId = agentId });
    }

    public async Task UpsertAsync(AgentHardwareInfo hardware)
    {
        hardware.UpdatedAt = DateTime.UtcNow;
        hardware.CollectedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM agent_hardware_info WHERE agent_id = @AgentId",
            new { hardware.AgentId });

        if (existing.HasValue)
        {
            hardware.Id = existing.Value;
            await conn.ExecuteAsync(
                """
                UPDATE agent_hardware_info SET manufacturer = @Manufacturer, model = @Model,
                       serial_number = @SerialNumber,
                       motherboard_manufacturer = @MotherboardManufacturer,
                       motherboard_model = @MotherboardModel,
                       motherboard_serial_number = @MotherboardSerialNumber,
                       processor = @Processor, processor_cores = @ProcessorCores,
                       processor_threads = @ProcessorThreads, processor_architecture = @ProcessorArchitecture,
                       total_memory_bytes = @TotalMemoryBytes,
                       bios_version = @BiosVersion, bios_manufacturer = @BiosManufacturer,
                       os_name = @OsName, os_version = @OsVersion, os_build = @OsBuild,
                       os_architecture = @OsArchitecture,
                       collected_at = @CollectedAt, updated_at = @UpdatedAt
                WHERE id = @Id
                """, hardware);
        }
        else
        {
            hardware.Id = IdGenerator.NewId();
            await conn.ExecuteAsync(
                """
                INSERT INTO agent_hardware_info (id, agent_id, manufacturer, model, serial_number,
                       motherboard_manufacturer, motherboard_model, motherboard_serial_number,
                       processor, processor_cores, processor_threads, processor_architecture,
                       total_memory_bytes, bios_version, bios_manufacturer,
                       os_name, os_version, os_build, os_architecture,
                       collected_at, updated_at)
                VALUES (@Id, @AgentId, @Manufacturer, @Model, @SerialNumber,
                       @MotherboardManufacturer, @MotherboardModel, @MotherboardSerialNumber,
                       @Processor, @ProcessorCores, @ProcessorThreads, @ProcessorArchitecture,
                       @TotalMemoryBytes, @BiosVersion, @BiosManufacturer,
                       @OsName, @OsVersion, @OsBuild, @OsArchitecture,
                       @CollectedAt, @UpdatedAt)
                """, hardware);
        }
    }

    public async Task<IEnumerable<DiskInfo>> GetDisksAsync(Guid agentId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<DiskInfo>(
            """
            SELECT id, agent_id AS AgentId, drive_letter AS DriveLetter, label, file_system AS FileSystem,
                   total_size_bytes AS TotalSizeBytes, free_space_bytes AS FreeSpaceBytes,
                   media_type AS MediaType, collected_at AS CollectedAt
            FROM disk_info WHERE agent_id = @AgentId
            """, new { AgentId = agentId });
    }

    public async Task ReplaceDiskInfoAsync(Guid agentId, IEnumerable<DiskInfo> disks)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync("DELETE FROM disk_info WHERE agent_id = @AgentId",
            new { AgentId = agentId }, tx);

        foreach (var disk in disks)
        {
            disk.Id = IdGenerator.NewId();
            disk.AgentId = agentId;
            disk.CollectedAt = DateTime.UtcNow;
            await conn.ExecuteAsync(
                """
                INSERT INTO disk_info (id, agent_id, drive_letter, label, file_system,
                       total_size_bytes, free_space_bytes, media_type, collected_at)
                VALUES (@Id, @AgentId, @DriveLetter, @Label, @FileSystem,
                       @TotalSizeBytes, @FreeSpaceBytes, @MediaType, @CollectedAt)
                """, disk, tx);
        }

        tx.Commit();
    }

    public async Task<IEnumerable<NetworkAdapterInfo>> GetNetworkAdaptersAsync(Guid agentId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<NetworkAdapterInfo>(
            """
            SELECT id, agent_id AS AgentId, name, mac_address AS MacAddress, ip_address AS IpAddress,
                   subnet_mask AS SubnetMask, gateway, dns_servers AS DnsServers,
                   is_dhcp_enabled AS IsDhcpEnabled, adapter_type AS AdapterType,
                   speed, collected_at AS CollectedAt
            FROM network_adapter_info WHERE agent_id = @AgentId
            """, new { AgentId = agentId });
    }

    public async Task ReplaceNetworkAdaptersAsync(Guid agentId, IEnumerable<NetworkAdapterInfo> adapters)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync("DELETE FROM network_adapter_info WHERE agent_id = @AgentId",
            new { AgentId = agentId }, tx);

        foreach (var adapter in adapters)
        {
            adapter.Id = IdGenerator.NewId();
            adapter.AgentId = agentId;
            adapter.CollectedAt = DateTime.UtcNow;
            await conn.ExecuteAsync(
                """
                INSERT INTO network_adapter_info (id, agent_id, name, mac_address, ip_address,
                       subnet_mask, gateway, dns_servers, is_dhcp_enabled, adapter_type, speed, collected_at)
                VALUES (@Id, @AgentId, @Name, @MacAddress, @IpAddress,
                       @SubnetMask, @Gateway, @DnsServers, @IsDhcpEnabled, @AdapterType, @Speed, @CollectedAt)
                """, adapter, tx);
        }

        tx.Commit();
    }

    public async Task<IEnumerable<MemoryModuleInfo>> GetMemoryModulesAsync(Guid agentId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<MemoryModuleInfo>(
            """
            SELECT id, agent_id AS AgentId, slot, capacity_bytes AS CapacityBytes,
                   speed_mhz AS SpeedMhz, memory_type AS MemoryType,
                   manufacturer, part_number AS PartNumber, serial_number AS SerialNumber,
                   collected_at AS CollectedAt
            FROM memory_module_info WHERE agent_id = @AgentId
            """, new { AgentId = agentId });
    }

    public async Task ReplaceMemoryModulesAsync(Guid agentId, IEnumerable<MemoryModuleInfo> modules)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync("DELETE FROM memory_module_info WHERE agent_id = @AgentId",
            new { AgentId = agentId }, tx);

        foreach (var module in modules)
        {
            module.Id = IdGenerator.NewId();
            module.AgentId = agentId;
            module.CollectedAt = DateTime.UtcNow;
            await conn.ExecuteAsync(
                """
                INSERT INTO memory_module_info (id, agent_id, slot, capacity_bytes, speed_mhz,
                       memory_type, manufacturer, part_number, serial_number, collected_at)
                VALUES (@Id, @AgentId, @Slot, @CapacityBytes, @SpeedMhz,
                       @MemoryType, @Manufacturer, @PartNumber, @SerialNumber, @CollectedAt)
                """, module, tx);
        }

        tx.Commit();
    }
}
