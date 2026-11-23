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

    public async Task<IReadOnlyDictionary<Guid, AgentHardwareInfo>> GetByAgentIdsAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken ct = default)
    {
        if (agentIds.Count == 0)
            return new Dictionary<Guid, AgentHardwareInfo>();

        var rows = await _db.AgentHardwareInfos
            .AsNoTracking()
            .Where(info => agentIds.Contains(info.AgentId))
            .ToListAsync(ct);

        return rows.ToDictionary(info => info.AgentId);
    }

    /// <summary>
    /// Carrega os discos de varios agentes em uma unica query, desserializando o
    /// HardwareComponentsJson em memoria. Usado pelo auto-labeling para avaliar
    /// regras de disco sem N+1.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DiskInfo>>> GetDisksByAgentIdsAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, IReadOnlyList<DiskInfo>>();
        if (agentIds.Count == 0)
            return result;

        var rows = await _db.AgentHardwareInfos
            .AsNoTracking()
            .Where(info => agentIds.Contains(info.AgentId))
            .Select(info => new { info.AgentId, info.HardwareComponentsJson })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            var components = DeserializeComponents(row.HardwareComponentsJson);
            result[row.AgentId] = components?.Disks ?? [];
        }

        return result;
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
            .AsNoTracking()
            .SingleOrDefaultAsync(info => info.AgentId == hardware.AgentId);

        if (existing is null)
        {
            hardware.Id = IdGenerator.NewId();
            _db.AgentHardwareInfos.Add(hardware);
            await _db.SaveChangesAsync();
            _db.Entry(hardware).State = EntityState.Detached;
            return;
        }

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
        existing.ProcessorTdpWatts = hardware.ProcessorTdpWatts;
        existing.ProcessorSocket = hardware.ProcessorSocket;
        existing.ProcessorFrequencyGhz = hardware.ProcessorFrequencyGhz;
        existing.ProcessorReleaseDate = hardware.ProcessorReleaseDate;
        existing.TotalMemoryBytes = hardware.TotalMemoryBytes;
        existing.GpuModel = hardware.GpuModel;
        existing.GpuMemoryBytes = hardware.GpuMemoryBytes;
        existing.GpuDriverVersion = hardware.GpuDriverVersion;
        existing.BiosVersion = hardware.BiosVersion;
        existing.BiosManufacturer = hardware.BiosManufacturer;
        existing.BiosDate = hardware.BiosDate;
        existing.BiosSerialNumber = hardware.BiosSerialNumber;
        existing.MachineScore = hardware.MachineScore;
        existing.OsName = hardware.OsName;
        existing.OsVersion = hardware.OsVersion;
        existing.OsBuild = hardware.OsBuild;
        existing.OsArchitecture = hardware.OsArchitecture;

        // ── Campos NULLABLE de identidade (M272) e de formato físico (M294) ──
        //
        // Copiados SÓ quando reportados. Motivo: o agente manda payloads
        // PARCIAIS (SyncNetworkConnections/SyncStartupAndScheduledTasks enviam
        // apenas Components, sem o objeto "hardware"), e esses campos são
        // NULLABLE justamente para distinguir "não reportado" de "falso".
        // Copiar null apagaria o valor aprendido — no caso do isLaptop, o selo
        // de notebook sumiria da listagem a cada sync de portas.
        //
        // Isso também corrige a whitelist: até aqui as colunas de SO (M272)
        // NUNCA eram copiadas no caminho de UPDATE, então agentes cujo registro
        // de hardware já existia ficavam com os_is_server/os_product_type NULL
        // para sempre (o site mascarava isso caindo na heurística de texto).
        if (hardware.OsEdition is not null) existing.OsEdition = hardware.OsEdition;
        if (hardware.IsServer is not null) existing.IsServer = hardware.IsServer;
        if (hardware.ProductType is not null) existing.ProductType = hardware.ProductType;
        if (hardware.Domain is not null) existing.Domain = hardware.Domain;
        if (hardware.Workgroup is not null) existing.Workgroup = hardware.Workgroup;
        if (hardware.PartOfDomain is not null) existing.PartOfDomain = hardware.PartOfDomain;

        if (hardware.IsLaptop is not null) existing.IsLaptop = hardware.IsLaptop;
        if (hardware.MachineType is not null) existing.MachineType = hardware.MachineType;
        if (hardware.ChassisType is not null) existing.ChassisType = hardware.ChassisType;
        if (hardware.PcSystemType is not null) existing.PcSystemType = hardware.PcSystemType;

        existing.InventoryRaw = hardware.InventoryRaw;
        existing.HardwareComponentsJson = hardware.HardwareComponentsJson;
        existing.InventorySchemaVersion = hardware.InventorySchemaVersion;
        existing.InventoryCollectedAt = hardware.InventoryCollectedAt;
        existing.CollectedAt = hardware.CollectedAt;
        existing.UpdatedAt = hardware.UpdatedAt;
        existing.TotalDisksCount = hardware.TotalDisksCount;

        var localTracked = _db.ChangeTracker.Entries<AgentHardwareInfo>()
            .FirstOrDefault(e => e.Entity.Id == existing.Id);
        if (localTracked is not null)
            localTracked.State = EntityState.Detached;

        _db.AgentHardwareInfos.Update(existing);
        await _db.SaveChangesAsync();
        _db.Entry(existing).State = EntityState.Detached;
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
