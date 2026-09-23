using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Inventory.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
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
                lp.Protocol, lp.Address, lp.Port, lp.State, lp.CollectedAt
            )).ToList(),
            OpenSockets: components.OpenSockets.Select(os => new AgentHardwareOpenSocketDto(
                os.ProcessName, os.ProcessId, os.ProcessPath,
                os.LocalAddress, os.LocalPort, os.RemoteAddress, os.RemotePort,
                os.Protocol, os.Family, os.State, os.CollectedAt
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
        s.CollectedAt,
        s.AvailableVersion,
        s.UpdateAvailable,
        s.UpdateSource,
        s.UpdatePackageId,
        s.InstallSource,
        ComputeUninstallAvailable(s));

    /// <summary>
    /// Indica se a desinstalação é viável com os dados do inventário: pacote
    /// reconhecido pelo gerenciador (winget/choco), ProductCode MSI ou um
    /// UninstallString do registro. O agent revalida e escolhe a estratégia.
    /// </summary>
    private static bool ComputeUninstallAvailable(AgentInstalledSoftware s)
    {
        if (!string.IsNullOrWhiteSpace(s.UpdatePackageId))
            return true;

        if (LooksLikeMsiProductCode(s.InstallId))
            return true;

        // "serial" costuma carregar o UninstallString quando não há ProductCode
        // (aceita .exe); "installSource" costuma ser o InstallLocation e exige
        // um indício de desinstalador.
        return LooksLikeUninstallCommand(s.Serial) || LooksLikeUninstallTarget(s.InstallSource);
    }

    private static bool LooksLikeMsiProductCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return trimmed.Length >= 32
            && trimmed.StartsWith('{')
            && trimmed.EndsWith('}')
            && trimmed.Contains('-');
    }

    private static bool LooksLikeUninstallCommand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.ToLowerInvariant();
        return normalized.Contains("uninstall") || normalized.Contains("msiexec") || normalized.Contains(".exe");
    }

    private static bool LooksLikeUninstallTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.ToLowerInvariant();
        return normalized.Contains("uninstall") || normalized.Contains("unins") || normalized.Contains("msiexec");
    }
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
        var updateCount = await softwareRepo.GetUpdateAvailableCountByAgentIdAsync(q.AgentId);

        return Result<AgentSoftwareSnapshotDto>.Success(new AgentSoftwareSnapshotDto(
            q.AgentId, snapshot?.TotalInstalled ?? 0, snapshot?.LastCollectedAt, updateCount));
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

        var includeNetwork = q.IncludeNetwork;
        return Result<AgentHardwareComponentsDto>.Success(new AgentHardwareComponentsDto(
            components.Printers.Select(p => new AgentHardwarePrinterDto(
                p.Name, p.DriverName, p.PortName, p.PrinterStatus,
                p.IsDefault, p.IsNetworkPrinter, p.Shared, p.ShareName, p.Location
            )).ToList(),
            includeNetwork
                ? components.ListeningPorts.Select(lp => new AgentHardwareListeningPortDto(
                    lp.ProcessName, lp.ProcessId, lp.ProcessPath, lp.Protocol,
                    lp.Address, lp.Port, lp.State, lp.CollectedAt
                )).ToList()
                : [],
            includeNetwork
                ? components.OpenSockets.Select(os => new AgentHardwareOpenSocketDto(
                    os.ProcessName, os.ProcessId, os.ProcessPath,
                    os.LocalAddress, os.LocalPort, os.RemoteAddress, os.RemotePort,
                    os.Protocol, os.Family, os.State, os.CollectedAt
                )).ToList()
                : [],
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
                si.Name, si.Path, si.Args, si.Type, si.Source, si.Status, si.Username, si.Detail, si.Hive
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

/// <summary>
/// Filtro de busca compartilhado das abas de rede do detalhe do agente — espelha
/// o filtro que antes era client-side (processo, PID, protocolo, endereços,
/// portas e, para sockets, estado).
/// </summary>
internal static class AgentNetworkPageFilter
{
    /// <summary>Estados TCP que representam conexão já encerrada (MIB_TCP_STATE).</summary>
    internal static readonly HashSet<string> ClosedTcpStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "TIME_WAIT", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "CLOSED"
    };

    public static bool MatchesListeningPort(ListeningPortInfo p, string search)
        => (p.ProcessName ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
           || p.ProcessId.ToString().Contains(search, StringComparison.Ordinal)
           || (p.Protocol ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
           || (p.Address ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
           || p.Port.ToString().Contains(search, StringComparison.Ordinal)
           || (p.ProcessPath ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesOpenSocket(OpenSocketInfo s, string search)
        => (s.ProcessName ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
           || s.ProcessId.ToString().Contains(search, StringComparison.Ordinal)
           || (s.Protocol ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
           || (s.LocalAddress ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
           || (s.RemoteAddress ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
           || s.LocalPort.ToString().Contains(search, StringComparison.Ordinal)
           || s.RemotePort.ToString().Contains(search, StringComparison.Ordinal)
           || (s.State ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Paginação por cursor (ponteiro) das portas em escuta do snapshot de
/// componentes do agente. O cursor é o índice do próximo item dentro da lista
/// filtrada/ordenada — evita devolver a lista inteira no browser.
/// </summary>
public sealed class GetAgentListeningPortsPageQueryHandler(
    IAgentRepository agentRepo,
    IAgentHardwareRepository hardwareRepo
) : IRequestHandler<GetAgentListeningPortsPageQuery, Result<AgentNetworkPageDto<AgentHardwareListeningPortDto>>>
{
    public async Task<Result<AgentNetworkPageDto<AgentHardwareListeningPortDto>>> Handle(
        GetAgentListeningPortsPageQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<AgentNetworkPageDto<AgentHardwareListeningPortDto>>.Failure(Error.NotFound("Agent not found."));

        var components = await hardwareRepo.GetComponentsAsync(q.AgentId);

        var search = (q.Search ?? string.Empty).Trim();
        IEnumerable<ListeningPortInfo> filtered = components.ListeningPorts;
        if (search.Length > 0)
            filtered = filtered.Where(p => AgentNetworkPageFilter.MatchesListeningPort(p, search));

        // Ordenação determinística: garante cursor estável entre requests sobre
        // o mesmo snapshot (o índice aponta para a posição na lista ordenada).
        var list = filtered
            .OrderBy(p => p.Port)
            .ThenBy(p => p.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Address, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProcessId)
            .ToList();

        var limit = Math.Clamp(q.Limit, 1, HardwareInventoryParser.MaxListeningPorts);

        var cursorIndex = 0;
        if (!string.IsNullOrWhiteSpace(q.Cursor)
            && !CursorPaginationHelper.TryDecodeIndexCursor(q.Cursor, out cursorIndex))
        {
            // NÃO repetir a 1ª página em cursor inválido (regressão conhecida do
            // software por cursor): falhar a request para o cliente reiniciar.
            return Result<AgentNetworkPageDto<AgentHardwareListeningPortDto>>.Failure(
                Error.Validation("cursor", "Cursor inválido para paginação de portas em escuta."));
        }
        var startIndex = cursorIndex;

        var pageItems = list.Skip(startIndex).Take(limit)
            .Select(p => new AgentHardwareListeningPortDto(
                p.ProcessName, p.ProcessId, p.ProcessPath,
                p.Protocol, p.Address, p.Port, p.State, p.CollectedAt))
            .ToList();
        var hasMore = startIndex + pageItems.Count < list.Count;
        string? nextCursor = hasMore && pageItems.Count > 0
            ? CursorPaginationHelper.EncodeIndexCursor(startIndex + pageItems.Count)
            : null;

        return Result<AgentNetworkPageDto<AgentHardwareListeningPortDto>>.Success(
            new AgentNetworkPageDto<AgentHardwareListeningPortDto>(
                pageItems, list.Count, q.Cursor, nextCursor, hasMore, q.Limit));
    }
}

/// <summary>
/// Paginação por cursor (ponteiro) das conexões abertas do snapshot de
/// componentes do agente, com estado TCP (ESTABLISHED, TIME_WAIT...).
/// </summary>
public sealed class GetAgentOpenSocketsPageQueryHandler(
    IAgentRepository agentRepo,
    IAgentHardwareRepository hardwareRepo
) : IRequestHandler<GetAgentOpenSocketsPageQuery, Result<AgentNetworkPageDto<AgentHardwareOpenSocketDto>>>
{
    public async Task<Result<AgentNetworkPageDto<AgentHardwareOpenSocketDto>>> Handle(
        GetAgentOpenSocketsPageQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<AgentNetworkPageDto<AgentHardwareOpenSocketDto>>.Failure(Error.NotFound("Agent not found."));

        var components = await hardwareRepo.GetComponentsAsync(q.AgentId);

        var search = (q.Search ?? string.Empty).Trim();
        IEnumerable<OpenSocketInfo> filtered = components.OpenSockets;
        if (search.Length > 0)
            filtered = filtered.Where(s => AgentNetworkPageFilter.MatchesOpenSocket(s, search));

        // Filtro por estado TCP. "open" exclui conexões já encerradas — é o que
        // remove o ruído do discovery-service.exe (TIME_WAIT em massa). Sockets
        // coletados por agentes antigos não têm estado; em "open" eles ficam
        // (não há como saber), mas em filtro exato não casam.
        var state = (q.State ?? string.Empty).Trim();
        if (state.Length > 0 && !state.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (state.Equals("open", StringComparison.OrdinalIgnoreCase))
                filtered = filtered.Where(s => s.State is null || !AgentNetworkPageFilter.ClosedTcpStates.Contains(s.State));
            else
                filtered = filtered.Where(s => string.Equals(s.State, state, StringComparison.OrdinalIgnoreCase));
        }

        // Ordenação determinística: garante cursor estável entre requests sobre
        // o mesmo snapshot (o índice aponta para a posição na lista ordenada).
        var list = filtered
            .OrderBy(s => s.LocalAddress, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.LocalPort)
            .ThenBy(s => s.RemoteAddress, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.RemotePort)
            .ThenBy(s => s.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.ProcessId)
            .ToList();

        var limit = Math.Clamp(q.Limit, 1, HardwareInventoryParser.MaxOpenSockets);

        var cursorIndex = 0;
        if (!string.IsNullOrWhiteSpace(q.Cursor)
            && !CursorPaginationHelper.TryDecodeIndexCursor(q.Cursor, out cursorIndex))
        {
            // NÃO repetir a 1ª página em cursor inválido (regressão conhecida do
            // software por cursor): falhar a request para o cliente reiniciar.
            return Result<AgentNetworkPageDto<AgentHardwareOpenSocketDto>>.Failure(
                Error.Validation("cursor", "Cursor inválido para paginação de conexões abertas."));
        }
        var startIndex = cursorIndex;

        var pageItems = list.Skip(startIndex).Take(limit)
            .Select(s => new AgentHardwareOpenSocketDto(
                s.ProcessName, s.ProcessId, s.ProcessPath,
                s.LocalAddress, s.LocalPort, s.RemoteAddress, s.RemotePort,
                s.Protocol, s.Family, s.State, s.CollectedAt))
            .ToList();
        var hasMore = startIndex + pageItems.Count < list.Count;
        string? nextCursor = hasMore && pageItems.Count > 0
            ? CursorPaginationHelper.EncodeIndexCursor(startIndex + pageItems.Count)
            : null;

        return Result<AgentNetworkPageDto<AgentHardwareOpenSocketDto>>.Success(
            new AgentNetworkPageDto<AgentHardwareOpenSocketDto>(
                pageItems, list.Count, q.Cursor, nextCursor, hasMore, q.Limit));
    }
}