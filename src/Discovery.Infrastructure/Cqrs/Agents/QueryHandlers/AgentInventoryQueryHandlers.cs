using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Inventory.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class GetAgentHardwareQueryHandler(
    IAgentRepository agentRepo,
    IAgentHardwareRepository hardwareRepo
) : IRequestHandler<GetAgentHardwareQuery, Result<AgentHardwareDto>>
{
    public async Task<Result<AgentHardwareDto>> Handle(GetAgentHardwareQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<AgentHardwareDto>.Failure(Error.NotFound("Agent not found."));

        var hardware = await hardwareRepo.GetByAgentIdAsync(q.AgentId);
        if (hardware is null)
            return Result<AgentHardwareDto>.Failure(Error.NotFound("Hardware info not found."));

        return Result<AgentHardwareDto>.Success(new AgentHardwareDto(
            hardware.Manufacturer ?? string.Empty,
            hardware.Model ?? string.Empty,
            hardware.SerialNumber,
            hardware.BiosVersion,
            hardware.BiosManufacturer,
            hardware.BiosDate,
            hardware.TotalMemoryBytes,
            hardware.ProcessorCores,
            hardware.ProcessorThreads,
            hardware.Processor,
            hardware.ProcessorArchitecture,
            hardware.ProcessorFrequencyGhz,
            hardware.MachineScore,
            hardware.GpuModel,
            hardware.GpuMemoryBytes,
            hardware.OsName,
            hardware.OsVersion,
            hardware.OsBuild,
            hardware.OsArchitecture
        ));
    }
}

/// <summary>
/// Handler para o DTO agregado <see cref="AgentHardwareReportDto"/>.
/// Combina hardware info + components em um unico payload, alinhado ao contrato
/// <c>HardwareReport</c> do frontend DiscoveryRMM_Site.
/// </summary>
public sealed class GetAgentHardwareReportQueryHandler(
    IAgentRepository agentRepo,
    IAgentHardwareRepository hardwareRepo
) : IRequestHandler<GetAgentHardwareReportQuery, Result<AgentHardwareReportDto>>
{
    public async Task<Result<AgentHardwareReportDto>> Handle(GetAgentHardwareReportQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<AgentHardwareReportDto>.Failure(Error.NotFound("Agent not found."));

        var hardware = await hardwareRepo.GetByAgentIdAsync(q.AgentId);
        AgentHardwareDto? hwDto = null;
        if (hardware is not null)
        {
            hwDto = new AgentHardwareDto(
                hardware.Manufacturer ?? string.Empty,
                hardware.Model ?? string.Empty,
                hardware.SerialNumber,
                hardware.BiosVersion,
                hardware.BiosManufacturer,
                hardware.BiosDate,
                hardware.TotalMemoryBytes,
                hardware.ProcessorCores,
                hardware.ProcessorThreads,
                hardware.Processor,
                hardware.ProcessorArchitecture,
                hardware.ProcessorFrequencyGhz,
                hardware.MachineScore,
                hardware.GpuModel,
                hardware.GpuMemoryBytes,
                hardware.OsName,
                hardware.OsVersion, hardware.OsBuild, hardware.OsArchitecture
            );
        }

        var components = await hardwareRepo.GetComponentsAsync(q.AgentId);

        return Result<AgentHardwareReportDto>.Success(new AgentHardwareReportDto(
            Hardware: hwDto,
            Printers: components.Printers.Select(p => new AgentHardwarePrinterDto(
                p.Name, p.DriverName, p.PortName, p.PrinterStatus,
                p.IsDefault, p.IsNetworkPrinter, p.Shared, p.ShareName, p.Location
            )).ToList(),
            ListeningPorts: components.ListeningPorts.Select(lp => new AgentHardwareListeningPortDto(
                lp.ProcessName, lp.ProcessId, lp.ProcessPath,
                lp.Protocol, lp.Address, lp.Port, lp.State
            )).ToList(),
            OpenSockets: components.OpenSockets.Select(os => new AgentHardwareOpenSocketDto(
                os.ProcessName, os.ProcessId, os.ProcessPath,
                os.LocalAddress, os.LocalPort, os.RemoteAddress, os.RemotePort,
                os.Protocol, os.Family
            )).ToList(),
            Disks: components.Disks.Select(d => new AgentHardwareDiskDto(
                d.DriveLetter, d.Label, d.FileSystem,
                d.TotalSizeBytes, d.FreeSpaceBytes, d.MediaType,
                d.SmartStatus, d.TemperatureC, d.PowerOnHours, d.ReallocatedSectors
            )).ToList(),
            NetworkAdapters: components.NetworkAdapters.Select(na => new AgentHardwareNetworkAdapterDto(
                na.Name, na.MacAddress, na.IpAddress, na.Ipv6Address, na.SubnetMask, na.Gateway,
                na.DnsServers is not null ? [na.DnsServers] : null,
                na.IsDhcpEnabled, na.AdapterType, na.Speed
            )).ToList(),
            MemoryModules: components.MemoryModules.Select(mm => new AgentHardwareMemoryModuleDto(
                mm.Manufacturer, mm.PartNumber, mm.SerialNumber,
                mm.CapacityBytes, mm.SpeedMhz, mm.MemoryType,
                mm.Slot, null, null
            )).ToList(),
            CollectedAt: hardware?.InventoryCollectedAt
        ));
    }
}

public sealed class GetAgentSoftwareQueryHandler(
    IAgentRepository agentRepo,
    IAgentSoftwareRepository softwareRepo
) : IRequestHandler<GetAgentSoftwareQuery, Result<CursorPageDto<AgentSoftwareItemDto>>>
{
    public async Task<Result<CursorPageDto<AgentSoftwareItemDto>>> Handle(GetAgentSoftwareQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<CursorPageDto<AgentSoftwareItemDto>>.Failure(Error.NotFound("Agent not found."));

        var limit = Math.Clamp(q.Limit, 1, 500);
        var page = await softwareRepo.GetCurrentByAgentIdPagedAsync(q.AgentId, q.Cursor, limit + 1, q.Search, q.Descending);

        var hasMore = page.Count > limit;
        var items = (hasMore ? page.Take(limit) : page)
            .Select(AgentSoftwareItemMappers.ToDto)
            .ToList().AsReadOnly();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            // IMPORTANTE: cursor DEVE ser codificado (Base64 "N") — o decoder
            // TryDecodeGuidCursor espera Base64. Cursor cru aqui quebrava a
            // paginação (decode falhava → repetia sempre a 1ª página → loop
            // infinito de requests no frontend). Fallback do decoder cobre
            // cursores crus legados já em circulação.
            // E DEVE apontar para o ÚLTIMO ITEM EMITIDO (items[^1]) — usar a
            // linha probe page[limit] (limit+1-ésima) pularia 1 item por página.
            nextCursor = CursorPaginationHelper.EncodeGuidCursor(items[^1].InventoryId);
        }

        return Result<CursorPageDto<AgentSoftwareItemDto>>.Success(
            new CursorPageDto<AgentSoftwareItemDto>(items, items.Count, q.Cursor, nextCursor, hasMore, q.Limit));
    }
}

/// <summary>
/// Mapeamento compartilhado AgentInstalledSoftware → AgentSoftwareItemDto
/// entre os handlers de paginação por cursor e por offset.
/// </summary>
public static class AgentSoftwareItemMappers
{
    public static AgentSoftwareItemDto ToDto(AgentInstalledSoftware s) => new(
        s.InventoryId,
        s.Name,
        s.Version,
        s.Publisher,
        s.Source,
        s.InstallId,
        s.Serial,
        s.InstallDate,
        s.CollectedAt);
}

public sealed class GetAgentSoftwarePageQueryHandler(
    IAgentRepository agentRepo,
    IAgentSoftwareRepository softwareRepo
) : IRequestHandler<GetAgentSoftwarePageQuery, Result<AgentSoftwarePageDto>>
{
    public async Task<Result<AgentSoftwarePageDto>> Handle(GetAgentSoftwarePageQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<AgentSoftwarePageDto>.Failure(Error.NotFound("Agent not found."));

        var safePage = Math.Max(1, q.Page);
        var safePageSize = Math.Clamp(q.PageSize, 1, 2000);

        var result = await softwareRepo.GetCurrentByAgentIdOffsetAsync(
            q.AgentId, safePage, safePageSize, q.Search, q.Descending, ct);

        var totalPages = Math.Max(1, (int)Math.Ceiling(result.TotalCount / (double)safePageSize));

        return Result<AgentSoftwarePageDto>.Success(new AgentSoftwarePageDto(
            result.Items.Select(AgentSoftwareItemMappers.ToDto).ToList(),
            result.TotalCount,
            safePage,
            safePageSize,
            totalPages));
    }
}

public sealed class GetAgentSoftwareSnapshotQueryHandler(
    IAgentRepository agentRepo,
    IAgentSoftwareRepository softwareRepo
) : IRequestHandler<GetAgentSoftwareSnapshotQuery, Result<AgentSoftwareSnapshotDto>>
{
    public async Task<Result<AgentSoftwareSnapshotDto>> Handle(GetAgentSoftwareSnapshotQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<AgentSoftwareSnapshotDto>.Failure(Error.NotFound("Agent not found."));

        var snapshot = await softwareRepo.GetSnapshotByAgentIdAsync(q.AgentId);
        return Result<AgentSoftwareSnapshotDto>.Success(new AgentSoftwareSnapshotDto(
            q.AgentId, snapshot?.TotalInstalled ?? 0, snapshot?.LastCollectedAt));
    }
}

public sealed class GetAgentHardwareComponentsQueryHandler(
    IAgentRepository agentRepo,
    IAgentHardwareRepository hardwareRepo
) : IRequestHandler<GetAgentHardwareComponentsQuery, Result<AgentHardwareComponentsDto>>
{
    public async Task<Result<AgentHardwareComponentsDto>> Handle(GetAgentHardwareComponentsQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<AgentHardwareComponentsDto>.Failure(Error.NotFound("Agent not found."));

        var components = await hardwareRepo.GetComponentsAsync(q.AgentId);
        var hardware = await hardwareRepo.GetByAgentIdAsync(q.AgentId);

        return Result<AgentHardwareComponentsDto>.Success(new AgentHardwareComponentsDto(
            components.Printers.Select(p => new AgentHardwarePrinterDto(
                p.Name, p.DriverName, p.PortName, p.PrinterStatus,
                p.IsDefault, p.IsNetworkPrinter, p.Shared, p.ShareName, p.Location
            )).ToList(),
            components.ListeningPorts.Select(lp => new AgentHardwareListeningPortDto(
                lp.ProcessName, lp.ProcessId, lp.ProcessPath, lp.Protocol,
                lp.Address, lp.Port, lp.State
            )).ToList(),
            components.OpenSockets.Select(os => new AgentHardwareOpenSocketDto(
                os.ProcessName, os.ProcessId, os.ProcessPath,
                os.LocalAddress, os.LocalPort, os.RemoteAddress, os.RemotePort,
                os.Protocol, os.Family
            )).ToList(),
            components.Disks.Select(d => new AgentHardwareDiskDto(
                d.DriveLetter, d.Label, d.FileSystem,
                d.TotalSizeBytes, d.FreeSpaceBytes, d.MediaType,
                d.SmartStatus, d.TemperatureC, d.PowerOnHours, d.ReallocatedSectors
            )).ToList(),
            components.NetworkAdapters.Select(na => new AgentHardwareNetworkAdapterDto(
                na.Name, na.MacAddress, na.IpAddress, na.Ipv6Address, na.SubnetMask,
                na.Gateway, na.DnsServers is not null ? [.. na.DnsServers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : null,
                na.IsDhcpEnabled, na.AdapterType, na.Speed
            )).ToList(),
            components.MemoryModules.Select(mm => new AgentHardwareMemoryModuleDto(
                mm.Manufacturer, mm.PartNumber, mm.SerialNumber,
                mm.CapacityBytes, mm.SpeedMhz, mm.MemoryType, mm.Slot,
                null, null
            )).ToList(),
            components.StartupItems.Select(si => new AgentStartupItemDto(
                si.Name, si.Path, si.Args, si.Type, si.Source, si.Status, si.Username, si.Detail
            )).ToList(),
            components.ScheduledTasks.Select(st => new AgentScheduledTaskDto(
                st.TaskPath, st.TaskName, st.State, st.Status, st.Author,
                st.ActionPath, st.ActionArgs, st.TriggerType, st.TriggerDesc,
                st.NextRunTime, st.LastRunTime, st.LastResult
            )).ToList(),
            hardware?.CollectedAt
        ));
    }
}