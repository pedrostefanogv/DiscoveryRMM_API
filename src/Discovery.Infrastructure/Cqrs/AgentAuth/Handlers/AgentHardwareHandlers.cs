using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Hardware;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentHardwareQueryHandler(
    IAgentHardwareRepository hardwareRepo
) : IRequestHandler<GetAgentHardwareQuery, Result<AgentHardwarePayloadDto>>
{
    public async Task<Result<AgentHardwarePayloadDto>> Handle(GetAgentHardwareQuery q, CancellationToken ct)
    {
        var hardware = await hardwareRepo.GetByAgentIdAsync(q.AgentId);
        var components = await hardwareRepo.GetComponentsAsync(q.AgentId);

        object? hardwareObj = null;
        if (hardware is not null)
        {
            hardwareObj = new
            {
                manufacturer = hardware.Manufacturer,
                model = hardware.Model,
                serial = hardware.SerialNumber,
                motherboardManufacturer = hardware.MotherboardManufacturer,
                motherboardModel = hardware.MotherboardModel,
                motherboardSerial = hardware.MotherboardSerialNumber,
                processor = hardware.Processor,
                processorCores = hardware.ProcessorCores,
                processorThreads = hardware.ProcessorThreads,
                processorArchitecture = hardware.ProcessorArchitecture,
                processorTdpWatts = hardware.ProcessorTdpWatts,
                processorSocket = hardware.ProcessorSocket,
                processorFrequencyGhz = hardware.ProcessorFrequencyGhz,
                processorReleaseDate = hardware.ProcessorReleaseDate,
                totalMemoryBytes = hardware.TotalMemoryBytes,
                gpuModel = hardware.GpuModel,
                gpuMemoryBytes = hardware.GpuMemoryBytes,
                gpuDriverVersion = hardware.GpuDriverVersion,
                biosVersion = hardware.BiosVersion,
                biosManufacturer = hardware.BiosManufacturer,
                biosDate = hardware.BiosDate,
                biosSerialNumber = hardware.BiosSerialNumber,
                osName = hardware.OsName,
                osVersion = hardware.OsVersion,
                osBuild = hardware.OsBuild,
                osArchitecture = hardware.OsArchitecture,
                machineScore = hardware.MachineScore
            };
        }

        var dto = new AgentHardwarePayloadDto(
            hardwareObj,
            components?.Disks,
            components?.NetworkAdapters,
            components?.MemoryModules,
            components?.Printers,
            components?.ListeningPorts,
            components?.OpenSockets
        );

        return Result<AgentHardwarePayloadDto>.Success(dto);
    }
}

public sealed class ReportAgentHardwareCommandHandler(
    IAgentRepository agentRepo,
    IAgentHardwareRepository hardwareRepo
) : IRequestHandler<ReportAgentHardwareCommand, Result<VoidResult>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<Result<VoidResult>> Handle(ReportAgentHardwareCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        // Update agent fields
        if (!string.IsNullOrWhiteSpace(cmd.Hostname) && agent.Hostname != cmd.Hostname)
            agent.Hostname = cmd.Hostname;
        if (!string.IsNullOrWhiteSpace(cmd.DisplayName))
            agent.DisplayName = cmd.DisplayName;
        if (!string.IsNullOrWhiteSpace(cmd.OperatingSystem))
            agent.OperatingSystem = cmd.OperatingSystem;
        if (!string.IsNullOrWhiteSpace(cmd.OsVersion))
            agent.OsVersion = cmd.OsVersion;
        if (!string.IsNullOrWhiteSpace(cmd.AgentVersion))
            agent.AgentVersion = cmd.AgentVersion;
        if (!string.IsNullOrWhiteSpace(cmd.LastIpAddress))
            agent.LastIpAddress = cmd.LastIpAddress;
        if (!string.IsNullOrWhiteSpace(cmd.MacAddress))
            agent.MacAddress = cmd.MacAddress;
        if (!string.IsNullOrWhiteSpace(cmd.MeshCentralNodeId))
            agent.MeshCentralNodeId = cmd.MeshCentralNodeId;

        await agentRepo.UpdateAsync(agent);

        // Build hardware info from command payload
        var hardwareInfo = new AgentHardwareInfo
        {
            AgentId = cmd.AgentId,
            InventoryRaw = cmd.InventoryRaw,
            InventorySchemaVersion = cmd.InventorySchemaVersion,
            InventoryCollectedAt = cmd.InventoryCollectedAt
        };

        // Parse hardware object (JSON-like from Agent)
        if (cmd.Hardware is JsonElement hw)
        {
            if (hw.TryGetProperty("manufacturer", out var m)) hardwareInfo.Manufacturer = m.GetString();
            if (hw.TryGetProperty("model", out var mod)) hardwareInfo.Model = mod.GetString();
            if (hw.TryGetProperty("serial", out var s)) hardwareInfo.SerialNumber = s.GetString();
            if (hw.TryGetProperty("motherboardManufacturer", out var mm)) hardwareInfo.MotherboardManufacturer = mm.GetString();
            if (hw.TryGetProperty("motherboardModel", out var mmod)) hardwareInfo.MotherboardModel = mmod.GetString();
            if (hw.TryGetProperty("motherboardSerial", out var ms)) hardwareInfo.MotherboardSerialNumber = ms.GetString();
            if (hw.TryGetProperty("processor", out var p)) hardwareInfo.Processor = p.GetString();
            if (hw.TryGetProperty("processorCores", out var pc) && pc.TryGetInt32(out var pcv)) hardwareInfo.ProcessorCores = pcv;
            if (hw.TryGetProperty("processorThreads", out var pt) && pt.TryGetInt32(out var ptv)) hardwareInfo.ProcessorThreads = ptv;
            if (hw.TryGetProperty("processorArchitecture", out var pa)) hardwareInfo.ProcessorArchitecture = pa.GetString();
            if (hw.TryGetProperty("processorTdpWatts", out var ptw) && ptw.TryGetInt32(out var ptwv)) hardwareInfo.ProcessorTdpWatts = ptwv;
            if (hw.TryGetProperty("processorSocket", out var ps)) hardwareInfo.ProcessorSocket = ps.GetString();
            if (hw.TryGetProperty("processorFrequencyGhz", out var pf) && pf.TryGetDecimal(out var pfv)) hardwareInfo.ProcessorFrequencyGhz = pfv;
            if (hw.TryGetProperty("processorReleaseDate", out var prd)) hardwareInfo.ProcessorReleaseDate = prd.GetString();
            if (hw.TryGetProperty("totalMemoryBytes", out var tmb) && tmb.TryGetInt64(out var tmbv)) hardwareInfo.TotalMemoryBytes = tmbv;
            if (hw.TryGetProperty("gpuModel", out var gm)) hardwareInfo.GpuModel = gm.GetString();
            if (hw.TryGetProperty("gpuMemoryBytes", out var gmb) && gmb.TryGetInt64(out var gmbv)) hardwareInfo.GpuMemoryBytes = gmbv;
            if (hw.TryGetProperty("gpuDriverVersion", out var gdv)) hardwareInfo.GpuDriverVersion = gdv.GetString();
            if (hw.TryGetProperty("biosVersion", out var bv)) hardwareInfo.BiosVersion = bv.GetString();
            if (hw.TryGetProperty("biosManufacturer", out var bm)) hardwareInfo.BiosManufacturer = bm.GetString();
            if (hw.TryGetProperty("biosDate", out var bd)) hardwareInfo.BiosDate = bd.GetString();
            if (hw.TryGetProperty("biosSerialNumber", out var bsn)) hardwareInfo.BiosSerialNumber = bsn.GetString();
            if (hw.TryGetProperty("osName", out var on)) hardwareInfo.OsName = on.GetString();
            if (hw.TryGetProperty("osVersion", out var ov)) hardwareInfo.OsVersion = ov.GetString();
            if (hw.TryGetProperty("osBuild", out var ob)) hardwareInfo.OsBuild = ob.GetString();
            if (hw.TryGetProperty("osArchitecture", out var oa)) hardwareInfo.OsArchitecture = oa.GetString();
        }

        // MachineScore: enviado pelo agent no nível raiz do envelope (cmd.MachineScore),
        // e também suportado dentro do objeto "hardware" aninhado para compatibilidade.
        if (cmd.MachineScore.HasValue)
            hardwareInfo.MachineScore = cmd.MachineScore.Value;

        if (cmd.Hardware is JsonElement hw2)
        {
            if (hw2.TryGetProperty("machineScore", out var ms2) && ms2.TryGetInt32(out var ms2v))
                hardwareInfo.MachineScore ??= ms2v;
        }

        if (cmd.Hardware is not null && hardwareInfo.MachineScore is null)
        {
            // Fallback: try to deserialize from object
            try
            {
                var json = JsonSerializer.Serialize(cmd.Hardware, JsonOptions);
                var parsed = JsonSerializer.Deserialize<AgentHardwareInfo>(json, JsonOptions);
                if (parsed is not null)
                {
                    parsed.AgentId = cmd.AgentId;
                    hardwareInfo = parsed;
                }
            }
            catch { /* keep defaults */ }
        }

        // Parse components
        AgentHardwareComponents? components = null;
        if (cmd.Components is JsonElement comp)
        {
            try
            {
                var compJson = comp.GetRawText();
                components = JsonSerializer.Deserialize<AgentHardwareComponents>(compJson, JsonOptions);
            }
            catch { /* invalid components, skip */ }
        }
        else if (cmd.Components is not null)
        {
            try
            {
                var compJson = JsonSerializer.Serialize(cmd.Components, JsonOptions);
                components = JsonSerializer.Deserialize<AgentHardwareComponents>(compJson, JsonOptions);
            }
            catch { /* invalid components, skip */ }
        }

        await hardwareRepo.UpsertAsync(hardwareInfo, components);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
