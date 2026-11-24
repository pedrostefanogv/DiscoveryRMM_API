using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

public partial class ReportDatasetQueryService
{
    private async Task<ReportQueryResult> QuerySoftwareInventoryAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var softwareName = GetString(filters, "softwareName");
        var orderBy = GetEnum(filters, "orderBy", SoftwareInventoryOrderBy.SoftwareName);
        var descending = GetSortDescending(filters, defaultValue: false);

        var query =
            from inv in _db.AgentSoftwareInventories.AsNoTracking()
            join ag in _db.Agents.AsNoTracking() on inv.AgentId equals ag.Id
            join st in _db.Sites.AsNoTracking() on ag.SiteId equals st.Id
            join cli in _db.Clients.AsNoTracking() on st.ClientId equals cli.Id
            join cat in _db.SoftwareCatalogs.AsNoTracking() on inv.SoftwareId equals cat.Id
            where inv.IsPresent && ag.DeletedAt == null && (!clientId.HasValue || st.ClientId == clientId.Value)
            select new
            {
                ClientName = cli.Name,
                SiteId = st.Id,
                SiteName = st.Name,
                AgentId = ag.Id,
                AgentHostname = ag.Hostname,
                SoftwareName = cat.Name,
                cat.Publisher,
                inv.Version,
                inv.LastSeenAt
            };

        if (siteId.HasValue)
            query = query.Where(x => x.SiteId == siteId.Value);

        if (agentId.HasValue)
            query = query.Where(x => x.AgentId == agentId.Value);

        if (!string.IsNullOrWhiteSpace(softwareName))
            query = query.Where(x => EF.Functions.ILike(x.SoftwareName, $"%{softwareName}%"));

        var orderedQuery = orderBy switch
        {
            SoftwareInventoryOrderBy.Publisher => descending
                ? query.OrderByDescending(x => x.Publisher).ThenByDescending(x => x.SoftwareName)
                : query.OrderBy(x => x.Publisher).ThenBy(x => x.SoftwareName),
            SoftwareInventoryOrderBy.Version => descending
                ? query.OrderByDescending(x => x.Version).ThenByDescending(x => x.SoftwareName)
                : query.OrderBy(x => x.Version).ThenBy(x => x.SoftwareName),
            SoftwareInventoryOrderBy.LastSeenAt => descending
                ? query.OrderByDescending(x => x.LastSeenAt).ThenBy(x => x.SoftwareName)
                : query.OrderBy(x => x.LastSeenAt).ThenBy(x => x.SoftwareName),
            SoftwareInventoryOrderBy.AgentHostname => descending
                ? query.OrderByDescending(x => x.AgentHostname).ThenBy(x => x.SoftwareName)
                : query.OrderBy(x => x.AgentHostname).ThenBy(x => x.SoftwareName),
            SoftwareInventoryOrderBy.SiteName => descending
                ? query.OrderByDescending(x => x.SiteName).ThenBy(x => x.SoftwareName)
                : query.OrderBy(x => x.SiteName).ThenBy(x => x.SoftwareName),
            _ => descending
                ? query.OrderByDescending(x => x.SoftwareName).ThenByDescending(x => x.AgentHostname)
                : query.OrderBy(x => x.SoftwareName).ThenBy(x => x.AgentHostname)
        };

        var rowsRaw = await orderedQuery
            .Take(limit)
            .ToListAsync(cancellationToken);

        var labelsByAgent = await GetAutomaticLabelsByAgentAsync(rowsRaw.Select(x => x.AgentId), cancellationToken);

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["clientName"] = x.ClientName,
                ["siteName"] = x.SiteName,
                ["agentId"] = x.AgentId,
                ["agentHostname"] = x.AgentHostname,
                ["automaticLabels"] = labelsByAgent.TryGetValue(x.AgentId, out var labels) ? labels : string.Empty,
                ["softwareName"] = x.SoftwareName,
                ["publisher"] = x.Publisher,
                ["version"] = x.Version,
                ["lastSeenAt"] = x.LastSeenAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["clientName", "siteName", "agentId", "agentHostname", "automaticLabels", "softwareName", "publisher", "version", "lastSeenAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryAgentHardwareAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var orderBy = GetEnum(filters, "orderBy", AgentHardwareOrderBy.SiteName);
        var descending = GetSortDescending(filters, defaultValue: false);

        var query =
            from hw in _db.AgentHardwareInfos.AsNoTracking()
            join ag in _db.Agents.AsNoTracking() on hw.AgentId equals ag.Id
            join st in _db.Sites.AsNoTracking() on ag.SiteId equals st.Id
            join cli in _db.Clients.AsNoTracking() on st.ClientId equals cli.Id
            where ag.DeletedAt == null && (!clientId.HasValue || st.ClientId == clientId.Value)
            select new
            {
                SiteId = st.Id,
                SiteName = st.Name,
                AgentId = ag.Id,
                ClientName = cli.Name,
                AgentHostname = ag.Hostname,
                // OS
                hw.OsName,
                hw.OsVersion,
                hw.OsBuild,
                hw.OsArchitecture,
                // Processador
                hw.Processor,
                hw.ProcessorCores,
                hw.ProcessorThreads,
                hw.ProcessorArchitecture,
                hw.ProcessorFrequencyGhz,
                hw.ProcessorSocket,
                hw.ProcessorTdpWatts,
                // Memória
                hw.TotalMemoryBytes,
                // GPU
                hw.GpuModel,
                hw.GpuMemoryBytes,
                hw.GpuDriverVersion,
                // Discos
                hw.TotalDisksCount,
                // Placa-mãe
                hw.Manufacturer,
                hw.Model,
                hw.SerialNumber,
                hw.MotherboardManufacturer,
                hw.MotherboardModel,
                hw.MotherboardSerialNumber,
                // BIOS
                hw.BiosVersion,
                hw.BiosManufacturer,
                hw.BiosDate,
                hw.BiosSerialNumber,
                // Metadata
                hw.CollectedAt,
                hw.InventoryCollectedAt,
                hw.InventorySchemaVersion
            };

        if (siteId.HasValue)
            query = query.Where(x => x.SiteId == siteId.Value);
        if (agentId.HasValue)
            query = query.Where(x => x.AgentId == agentId.Value);

        var orderedQuery = orderBy switch
        {
            AgentHardwareOrderBy.AgentHostname => descending
                ? query.OrderByDescending(x => x.AgentHostname).ThenBy(x => x.SiteName)
                : query.OrderBy(x => x.AgentHostname).ThenBy(x => x.SiteName),
            AgentHardwareOrderBy.CollectedAt => descending
                ? query.OrderByDescending(x => x.CollectedAt).ThenBy(x => x.SiteName)
                : query.OrderBy(x => x.CollectedAt).ThenBy(x => x.SiteName),
            AgentHardwareOrderBy.OsName => descending
                ? query.OrderByDescending(x => x.OsName).ThenBy(x => x.SiteName)
                : query.OrderBy(x => x.OsName).ThenBy(x => x.SiteName),
            _ => descending
                ? query.OrderByDescending(x => x.SiteName).ThenByDescending(x => x.AgentHostname)
                : query.OrderBy(x => x.SiteName).ThenBy(x => x.AgentHostname)
        };

        var rowsRaw = await orderedQuery
            .Take(limit)
            .ToListAsync(cancellationToken);

        var labelsByAgent = await GetAutomaticLabelsByAgentAsync(rowsRaw.Select(x => x.AgentId), cancellationToken);

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["clientName"] = x.ClientName,
                ["agentId"] = x.AgentId,
                ["siteName"] = x.SiteName,
                ["agentHostname"] = x.AgentHostname,
                ["automaticLabels"] = labelsByAgent.TryGetValue(x.AgentId, out var labels) ? labels : string.Empty,
                // OS Info
                ["osName"] = x.OsName,
                ["osVersion"] = x.OsVersion,
                ["osBuild"] = x.OsBuild,
                ["osArchitecture"] = x.OsArchitecture,
                // Processor Info
                ["processor"] = x.Processor,
                ["processorCores"] = x.ProcessorCores,
                ["processorThreads"] = x.ProcessorThreads,
                ["processorArchitecture"] = x.ProcessorArchitecture,
                ["processorFrequencyGhz"] = x.ProcessorFrequencyGhz,
                ["processorSocket"] = x.ProcessorSocket,
                ["processorTdpWatts"] = x.ProcessorTdpWatts,
                // Memory Info
                ["totalMemoryGB"] = x.TotalMemoryBytes.HasValue ? decimal.Round(x.TotalMemoryBytes.Value / 1024m / 1024m / 1024m, 2) : (object?)null,
                ["totalMemoryBytes"] = x.TotalMemoryBytes,
                // GPU Info
                ["gpuModel"] = x.GpuModel,
                ["gpuMemoryGB"] = x.GpuMemoryBytes.HasValue ? decimal.Round(x.GpuMemoryBytes.Value / 1024m / 1024m / 1024m, 2) : (object?)null,
                ["gpuDriverVersion"] = x.GpuDriverVersion,
                // Disk Info
                ["totalDisksCount"] = x.TotalDisksCount,
                // Motherboard Info
                ["manufacturer"] = x.Manufacturer,
                ["model"] = x.Model,
                ["serialNumber"] = x.SerialNumber,
                ["motherboardManufacturer"] = x.MotherboardManufacturer,
                ["motherboardModel"] = x.MotherboardModel,
                ["motherboardSerialNumber"] = x.MotherboardSerialNumber,
                // BIOS Info
                ["biosVersion"] = x.BiosVersion,
                ["biosManufacturer"] = x.BiosManufacturer,
                ["biosDate"] = x.BiosDate,
                ["biosSerialNumber"] = x.BiosSerialNumber,
                // Metadata
                ["collectedAt"] = x.CollectedAt,
                ["inventoryCollectedAt"] = x.InventoryCollectedAt,
                ["inventorySchemaVersion"] = x.InventorySchemaVersion
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["clientName", "siteName", "agentId", "agentHostname", "automaticLabels", "osName", "osVersion", "osBuild", "osArchitecture", "processor", "processorCores", "processorThreads", "processorArchitecture", "processorFrequencyGhz", "processorSocket", "processorTdpWatts", "totalMemoryGB", "totalMemoryBytes", "gpuModel", "gpuMemoryGB", "gpuDriverVersion", "totalDisksCount", "manufacturer", "model", "serialNumber", "motherboardManufacturer", "motherboardModel", "motherboardSerialNumber", "biosVersion", "biosManufacturer", "biosDate", "biosSerialNumber", "inventorySchemaVersion", "inventoryCollectedAt", "collectedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryAgentInventoryCompositeAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var softwareName = GetString(filters, "softwareName");
        var orderBy = GetEnum(filters, "orderBy", AgentInventoryCompositeOrderBy.AgentHostname);
        var descending = GetSortDescending(filters, defaultValue: false);

        var query =
            from inv in _db.AgentSoftwareInventories.AsNoTracking()
            join ag in _db.Agents.AsNoTracking() on inv.AgentId equals ag.Id
            join st in _db.Sites.AsNoTracking() on ag.SiteId equals st.Id
            join cli in _db.Clients.AsNoTracking() on st.ClientId equals cli.Id
            join cat in _db.SoftwareCatalogs.AsNoTracking() on inv.SoftwareId equals cat.Id
            join hw in _db.AgentHardwareInfos.AsNoTracking() on ag.Id equals hw.AgentId into hardwareJoin
            from hw in hardwareJoin.DefaultIfEmpty()
            where inv.IsPresent && ag.DeletedAt == null && (!clientId.HasValue || st.ClientId == clientId.Value)
            select new
            {
                ClientName = cli.Name,
                SiteId = st.Id,
                SiteName = st.Name,
                AgentId = ag.Id,
                AgentHostname = ag.Hostname,
                SoftwareName = cat.Name,
                Publisher = cat.Publisher,
                SoftwareVersion = inv.Version,
                SoftwareLastSeenAt = inv.LastSeenAt,
                OsName = hw != null ? hw.OsName : null,
                OsVersion = hw != null ? hw.OsVersion : null,
                Processor = hw != null ? hw.Processor : null,
                TotalMemoryBytes = hw != null ? hw.TotalMemoryBytes : null,
                TotalDisksCount = hw != null ? hw.TotalDisksCount : null,
                HardwareCollectedAt = hw != null ? hw.CollectedAt : (DateTime?)null
            };

        if (siteId.HasValue)
            query = query.Where(x => x.SiteId == siteId.Value);

        if (agentId.HasValue)
            query = query.Where(x => x.AgentId == agentId.Value);

        if (!string.IsNullOrWhiteSpace(softwareName))
            query = query.Where(x => EF.Functions.ILike(x.SoftwareName, $"%{softwareName}%"));

        var orderedQuery = orderBy switch
        {
            AgentInventoryCompositeOrderBy.SiteName => descending
                ? query.OrderByDescending(x => x.SiteName).ThenByDescending(x => x.AgentHostname)
                : query.OrderBy(x => x.SiteName).ThenBy(x => x.AgentHostname),
            AgentInventoryCompositeOrderBy.SoftwareName => descending
                ? query.OrderByDescending(x => x.SoftwareName).ThenByDescending(x => x.AgentHostname)
                : query.OrderBy(x => x.SoftwareName).ThenBy(x => x.AgentHostname),
            AgentInventoryCompositeOrderBy.CollectedAt => descending
                ? query.OrderByDescending(x => x.HardwareCollectedAt).ThenBy(x => x.AgentHostname)
                : query.OrderBy(x => x.HardwareCollectedAt).ThenBy(x => x.AgentHostname),
            _ => descending
                ? query.OrderByDescending(x => x.AgentHostname).ThenByDescending(x => x.SoftwareName)
                : query.OrderBy(x => x.AgentHostname).ThenBy(x => x.SoftwareName)
        };

        var rowsRaw = await orderedQuery
            .Take(limit)
            .ToListAsync(cancellationToken);

        var labelsByAgent = await GetAutomaticLabelsByAgentAsync(rowsRaw.Select(x => x.AgentId), cancellationToken);

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["clientName"] = x.ClientName,
                ["siteName"] = x.SiteName,
                ["agentId"] = x.AgentId,
                ["agentHostname"] = x.AgentHostname,
                ["automaticLabels"] = labelsByAgent.TryGetValue(x.AgentId, out var labels) ? labels : string.Empty,
                ["osName"] = x.OsName,
                ["osVersion"] = x.OsVersion,
                ["processor"] = x.Processor,
                ["totalMemoryGB"] = x.TotalMemoryBytes.HasValue ? decimal.Round(x.TotalMemoryBytes.Value / 1024m / 1024m / 1024m, 2) : (object?)null,
                ["totalDisksCount"] = x.TotalDisksCount,
                ["softwareName"] = x.SoftwareName,
                ["publisher"] = x.Publisher,
                ["softwareVersion"] = x.SoftwareVersion,
                ["softwareLastSeenAt"] = x.SoftwareLastSeenAt,
                ["hardwareCollectedAt"] = x.HardwareCollectedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["clientName", "siteName", "agentId", "agentHostname", "automaticLabels", "osName", "osVersion", "processor", "totalMemoryGB", "totalDisksCount", "softwareName", "publisher", "softwareVersion", "softwareLastSeenAt", "hardwareCollectedAt"],
            Rows = rows
        };
    }

    /// <summary>
    /// Limite de agentes materializados ao derivar datasets de componentes do
    /// JSON de <c>agent_hardware_info.hardware_components_json</c>.
    /// </summary>
    private const int MaxAgentsForComponentDataset = 20000;

    private static readonly JsonSerializerOptions ComponentJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ComponentSourceRow(
        Guid AgentId,
        string AgentHostname,
        Guid SiteId,
        string SiteName,
        string ClientName,
        AgentHardwareComponents? Components);

    /// <summary>
    /// As tabelas dedicadas de hardware (disk_info/network_adapter_info/...
    /// e as _infos do EF) não existem mais — a M052 removeu as antigas e nunca
    /// houve migration para as plurais. Os componentes vivem no JSON.
    /// </summary>
    private async Task<List<ComponentSourceRow>> LoadComponentSourceRowsAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        CancellationToken cancellationToken)
    {
        var query =
            from hw in _db.AgentHardwareInfos.AsNoTracking()
            join ag in _db.Agents.AsNoTracking() on hw.AgentId equals ag.Id
            join st in _db.Sites.AsNoTracking() on ag.SiteId equals st.Id
            join cli in _db.Clients.AsNoTracking() on st.ClientId equals cli.Id
            where ag.DeletedAt == null && (!clientId.HasValue || st.ClientId == clientId.Value)
            select new
            {
                AgentId = ag.Id,
                AgentHostname = ag.Hostname,
                SiteId = st.Id,
                SiteName = st.Name,
                ClientName = cli.Name,
                hw.HardwareComponentsJson
            };

        if (siteId.HasValue)
            query = query.Where(x => x.SiteId == siteId.Value);
        if (agentId.HasValue)
            query = query.Where(x => x.AgentId == agentId.Value);

        // Ordenação determinística antes do corte: sem ela, o Take devolve um
        // subconjunto diferente a cada execução (relatórios ficam inconsistentes).
        var rows = await query
            .OrderBy(row => row.AgentHostname)
            .ThenBy(row => row.AgentId)
            .Take(MaxAgentsForComponentDataset)
            .ToListAsync(cancellationToken);
        return rows
            .Select(row => new ComponentSourceRow(
                row.AgentId,
                row.AgentHostname,
                row.SiteId,
                row.SiteName,
                row.ClientName,
                DeserializeComponents(row.HardwareComponentsJson)))
            .ToList();
    }

    private static AgentHardwareComponents? DeserializeComponents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentHardwareComponents>(json, ComponentJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ReportQueryResult> QueryAgentDisksAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var descending = GetSortDescending(filters, defaultValue: false);

        var sources = await LoadComponentSourceRowsAsync(clientId, siteId, agentId, cancellationToken);
        var entries = sources.SelectMany(source =>
            (source.Components?.Disks ?? new List<DiskInfo>()).Select(disk => (Source: source, Disk: disk)));

        var ordered = descending
            ? entries.OrderByDescending(entry => entry.Disk.SizeBytes)
            : entries.OrderBy(entry => entry.Disk.Interface).ThenBy(entry => entry.Disk.Name);

        var rows = ordered.Take(limit).Select(entry =>
        {
            var source = entry.Source;
            var disk = entry.Disk;
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["agentId"] = source.AgentId, ["agentHostname"] = source.AgentHostname,
                ["siteName"] = source.SiteName, ["clientName"] = source.ClientName,
                ["diskName"] = disk.Name, ["sizeBytes"] = disk.SizeBytes,
                ["freeBytes"] = disk.FreeBytes, ["fileSystem"] = disk.FileSystem,
                ["interface"] = disk.Interface, ["type"] = disk.Type,
                ["serialNumber"] = disk.SerialNumber, ["healthStatus"] = disk.HealthStatus,
                ["collectedAt"] = disk.CollectedAt
            };
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["agentId", "agentHostname", "siteName", "clientName", "diskName", "sizeBytes", "freeBytes", "fileSystem", "interface", "type", "serialNumber", "healthStatus", "collectedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryNetworkAdaptersAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var descending = GetSortDescending(filters, defaultValue: false);

        var sources = await LoadComponentSourceRowsAsync(clientId, siteId, agentId, cancellationToken);
        var entries = sources.SelectMany(source =>
            (source.Components?.NetworkAdapters ?? new List<NetworkAdapterInfo>()).Select(adapter => (Source: source, Adapter: adapter)));

        var ordered = descending
            ? entries.OrderByDescending(entry => entry.Adapter.Name)
            : entries.OrderBy(entry => entry.Adapter.Name);

        var rows = ordered.Take(limit).Select(entry =>
        {
            var source = entry.Source;
            var adapter = entry.Adapter;
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["agentId"] = source.AgentId, ["agentHostname"] = source.AgentHostname,
                ["siteName"] = source.SiteName, ["clientName"] = source.ClientName,
                ["adapterName"] = adapter.Name, ["macAddress"] = adapter.MacAddress,
                ["ipAddresses"] = string.Join(", ", adapter.IpAddresses),
                ["speedMbps"] = adapter.SpeedMbps, ["isDefault"] = adapter.IsDefault,
                ["collectedAt"] = adapter.CollectedAt
            };
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["agentId", "agentHostname", "siteName", "clientName", "adapterName", "macAddress", "ipAddresses", "speedMbps", "isDefault", "collectedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryListeningPortsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var port = GetInt(filters, "port");
        var processName = GetString(filters, "processName");
        var state = GetString(filters, "state");
        var descending = GetSortDescending(filters, defaultValue: false);

        var sources = await LoadComponentSourceRowsAsync(clientId, siteId, agentId, cancellationToken);
        IEnumerable<(ComponentSourceRow Source, ListeningPortInfo Port)> entries = sources.SelectMany(source =>
            (source.Components?.ListeningPorts ?? new List<ListeningPortInfo>())
                .Select(listeningPort => (Source: source, Port: listeningPort)));

        if (port.HasValue)
            entries = entries.Where(entry => entry.Port.Port == port.Value);
        if (!string.IsNullOrWhiteSpace(processName))
        {
            var processFilter = processName;
            entries = entries.Where(entry => entry.Port.ProcessName != null && entry.Port.ProcessName.Contains(processFilter));
        }
        if (!string.IsNullOrWhiteSpace(state))
        {
            var stateFilter = state;
            entries = entries.Where(entry => entry.Port.State != null && entry.Port.State.Contains(stateFilter));
        }

        var ordered = descending
            ? entries.OrderByDescending(entry => entry.Port.Port)
            : entries.OrderBy(entry => entry.Port.Port);

        var rows = ordered.Take(limit).Select(entry =>
        {
            var source = entry.Source;
            var listeningPort = entry.Port;
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["agentId"] = source.AgentId, ["agentHostname"] = source.AgentHostname,
                ["siteName"] = source.SiteName, ["clientName"] = source.ClientName,
                ["port"] = listeningPort.Port, ["protocol"] = listeningPort.Protocol,
                ["processName"] = listeningPort.ProcessName, ["state"] = listeningPort.State,
                ["collectedAt"] = listeningPort.CollectedAt
            };
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["agentId", "agentHostname", "siteName", "clientName", "port", "protocol", "processName", "state", "collectedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryPrintersAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var descending = GetSortDescending(filters, defaultValue: false);

        var sources = await LoadComponentSourceRowsAsync(clientId, siteId, agentId, cancellationToken);
        var entries = sources.SelectMany(source =>
            (source.Components?.Printers ?? new List<PrinterInfo>()).Select(printer => (Source: source, Printer: printer)));

        var ordered = descending
            ? entries.OrderByDescending(entry => entry.Printer.Name)
            : entries.OrderBy(entry => entry.Printer.Name);

        var rows = ordered.Take(limit).Select(entry =>
        {
            var source = entry.Source;
            var printer = entry.Printer;
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["agentId"] = source.AgentId, ["agentHostname"] = source.AgentHostname,
                ["siteName"] = source.SiteName, ["clientName"] = source.ClientName,
                ["printerName"] = printer.Name, ["driverName"] = printer.DriverName,
                ["portName"] = printer.PortName, ["isDefault"] = printer.IsDefault,
                ["isShared"] = printer.IsShared, ["collectedAt"] = printer.CollectedAt
            };
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["agentId", "agentHostname", "siteName", "clientName", "printerName", "driverName", "portName", "isDefault", "isShared", "collectedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QuerySoftwareCatalogAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var publisher = GetString(filters, "publisher");
        var descending = GetSortDescending(filters, defaultValue: false);

        var query = _db.SoftwareCatalogs.AsNoTracking().AsQueryable();
        // Filtro por categoria desabilitado — coluna 'category' ainda não existe no banco (Ignored no EF)
        if (!string.IsNullOrWhiteSpace(publisher)) query = query.Where(x => x.Publisher == publisher);

        query = descending
            ? query.OrderByDescending(x => x.Name)
            : query.OrderBy(x => x.Name);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["id"] = x.Id, ["name"] = x.Name, ["publisher"] = x.Publisher,
            // Report fields — colunas ainda não existem no banco; retornar null até migração
            ["category"] = (object?)null,
            ["latestVersion"] = (object?)null,
            ["eolDate"] = (object?)null,
            ["isEol"] = (object?)null,
            ["licenseType"] = (object?)null,
            ["updatedAt"] = x.UpdatedAt
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "name", "publisher", "category", "latestVersion", "eolDate", "isEol", "licenseType", "updatedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryAppPackagesAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var source = GetString(filters, "source");
        var category = GetString(filters, "category");
        var descending = GetSortDescending(filters, defaultValue: false);

        var query = _db.AppPackages.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(source))
        {
            var normalizedSource = source.Trim().ToLowerInvariant();
            query = query.Where(x => x.Source != null && x.Source.ToLower() == normalizedSource);
        }
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category);

        query = descending
            ? query.OrderByDescending(x => x.UpdatedAt)
            : query.OrderBy(x => x.Name);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["id"] = x.Id, ["name"] = x.Name, ["publisher"] = x.Publisher,
            ["version"] = x.Version, ["source"] = x.Source,
            ["category"] = x.Category, ["isActive"] = x.IsActive,
            ["description"] = x.Description, ["updatedAt"] = x.UpdatedAt
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "name", "publisher", "version", "source", "category", "isActive", "description", "updatedAt"],
            Rows = rows
        };
    }
}
