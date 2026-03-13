using System.Text.Json;
using System.Text.Json.Nodes;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Services;

public class ReportDatasetQueryService : IReportDatasetQueryService
{
    private const int DefaultLimit = 1000;
    private const int MaxLimit = 10000;

    private readonly MeduzaDbContext _db;

    public ReportDatasetQueryService(MeduzaDbContext db)
    {
        _db = db;
    }

    public async Task<ReportQueryResult> QueryAsync(ReportTemplate template, string? filtersJson, CancellationToken cancellationToken = default)
    {
        var layout = ReportLayoutDefinitionParser.ParseOrDefault(template.LayoutJson);
        if (layout.DataSources is { Count: > 1 })
            return await QueryMultiSourceAsync(layout.DataSources, filtersJson, cancellationToken);

        var filters = ParseFilters(filtersJson);
        var clientId = GetGuid(filters, "clientId");

        return await QuerySingleDatasetAsync(template.DatasetType, clientId, filters, cancellationToken);
    }

    private async Task<ReportQueryResult> QuerySingleDatasetAsync(ReportDatasetType datasetType, Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        return datasetType switch
        {
            ReportDatasetType.SoftwareInventory => await QuerySoftwareInventoryAsync(clientId, filters, cancellationToken),
            ReportDatasetType.Logs => await QueryLogsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.ConfigurationAudit => await QueryConfigurationAuditAsync(filters, cancellationToken),
            ReportDatasetType.Tickets => await QueryTicketsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AgentHardware => await QueryAgentHardwareAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AgentInventoryComposite => await QueryAgentInventoryCompositeAsync(clientId, filters, cancellationToken),
            _ => new ReportQueryResult { Columns = ["message"], Rows = [new Dictionary<string, object?> { ["message"] = "Dataset not supported." }] }
        };
    }

    private async Task<ReportQueryResult> QueryMultiSourceAsync(IReadOnlyList<ReportLayoutDataSourceDefinition> dataSources, string? runtimeFiltersJson, CancellationToken cancellationToken)
    {
        var normalizedSources = dataSources
            .Where(source => source.DatasetType.HasValue && !string.IsNullOrWhiteSpace(source.Alias))
            .ToList();

        if (normalizedSources.Count == 0)
            return new ReportQueryResult { Columns = ["message"], Rows = [new Dictionary<string, object?> { ["message"] = "No valid dataSources were provided." }] };

        var globalFilters = ParseJsonObject(runtimeFiltersJson);
        var sourceResults = new List<MultiSourceResult>(normalizedSources.Count);

        foreach (var source in normalizedSources)
        {
            var mergedFilters = MergeJsonObjects(globalFilters, ParseJsonObject(source.Filters));
            var mergedFiltersJson = mergedFilters?.ToJsonString();
            var mergedFiltersElement = ParseFilters(mergedFiltersJson);
            var clientId = GetGuid(mergedFiltersElement, "clientId");

            var result = await QuerySingleDatasetAsync(source.DatasetType!.Value, clientId, mergedFiltersElement, cancellationToken);
            sourceResults.Add(new MultiSourceResult(source, result));
        }

        if (sourceResults.Count == 1)
            return sourceResults[0].Result;

        var baseSource = sourceResults[0];
        var mergedRows = baseSource.Result.Rows
            .Select(row => BuildBaseRow(baseSource.Source.Alias!, row))
            .ToList();

        for (var index = 1; index < sourceResults.Count; index++)
        {
            var current = sourceResults[index];
            var join = current.Source.Join;
            if (join is null || string.IsNullOrWhiteSpace(join.SourceKey))
                continue;

            var sourceKey = join.SourceKey;
            var targetKey = string.IsNullOrWhiteSpace(join.TargetKey) ? sourceKey : join.TargetKey!;
            var joinAlias = string.IsNullOrWhiteSpace(join.JoinToAlias) ? baseSource.Source.Alias! : join.JoinToAlias!;
            var joinType = string.Equals(join.JoinType, "inner", StringComparison.OrdinalIgnoreCase) ? "inner" : "left";

            var lookup = current.Result.Rows
                .GroupBy(row => GetJoinKey(row, sourceKey))
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key!, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            var nextRows = new List<Dictionary<string, object?>>();
            foreach (var left in mergedRows)
            {
                var leftKey = GetJoinKey(left, targetKey);
                if (string.IsNullOrWhiteSpace(leftKey))
                {
                    if (joinType == "left")
                        nextRows.Add(left);
                    continue;
                }

                if (!lookup.TryGetValue(leftKey, out var matches) || matches.Count == 0)
                {
                    if (joinType == "left")
                        nextRows.Add(left);
                    continue;
                }

                foreach (var match in matches)
                {
                    var merged = new Dictionary<string, object?>(left, StringComparer.OrdinalIgnoreCase);
                    foreach (var field in match.Keys)
                    {
                        merged[$"{current.Source.Alias}.{field}"] = match[field];
                    }

                    // Atalho útil para join com alias explícito no target (ex.: hw.agentId)
                    merged[$"{joinAlias}.{targetKey}"] = left.TryGetValue($"{joinAlias}.{targetKey}", out var targetValue)
                        ? targetValue
                        : left.TryGetValue(targetKey, out var plainValue) ? plainValue : null;

                    nextRows.Add(merged);
                }
            }

            mergedRows = nextRows;
        }

        var columns = new List<string>();
        foreach (var column in baseSource.Result.Columns)
        {
            if (!columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                columns.Add(column);

            var aliased = $"{baseSource.Source.Alias}.{column}";
            if (!columns.Contains(aliased, StringComparer.OrdinalIgnoreCase))
                columns.Add(aliased);
        }

        for (var index = 1; index < sourceResults.Count; index++)
        {
            var source = sourceResults[index];
            foreach (var column in source.Result.Columns)
            {
                var aliased = $"{source.Source.Alias}.{column}";
                if (!columns.Contains(aliased, StringComparer.OrdinalIgnoreCase))
                    columns.Add(aliased);
            }
        }

        return new ReportQueryResult
        {
            Columns = columns,
            Rows = mergedRows.Cast<IReadOnlyDictionary<string, object?>>().ToList()
        };
    }

    private static Dictionary<string, object?> BuildBaseRow(string alias, IReadOnlyDictionary<string, object?> row)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in row.Keys)
        {
            result[field] = row[field];
            result[$"{alias}.{field}"] = row[field];
        }

        return result;
    }

    private static string? GetJoinKey(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var value) && value is not null)
            return value.ToString();

        // tenta resolver formato alias.key quando key vier sem alias
        var dottedCandidate = row.Keys.FirstOrDefault(current => current.EndsWith($".{key}", StringComparison.OrdinalIgnoreCase));
        if (dottedCandidate is not null && row.TryGetValue(dottedCandidate, out var dottedValue) && dottedValue is not null)
            return dottedValue.ToString();

        return null;
    }

    private static JsonObject? ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? ParseJsonObject(JsonElement? jsonElement)
    {
        if (!jsonElement.HasValue || jsonElement.Value.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            return JsonNode.Parse(jsonElement.Value.GetRawText()) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? MergeJsonObjects(JsonObject? baseObject, JsonObject? overrides)
    {
        if (baseObject is null && overrides is null)
            return null;

        var result = new JsonObject();
        if (baseObject is not null)
        {
            foreach (var property in baseObject)
                result[property.Key] = property.Value?.DeepClone();
        }

        if (overrides is not null)
        {
            foreach (var property in overrides)
                result[property.Key] = property.Value?.DeepClone();
        }

        return result;
    }

    private sealed record MultiSourceResult(ReportLayoutDataSourceDefinition Source, ReportQueryResult Result);

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
            where inv.IsPresent && (!clientId.HasValue || st.ClientId == clientId.Value)
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

    private async Task<ReportQueryResult> QueryLogsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");
        var orderBy = GetEnum(filters, "orderBy", LogsOrderBy.Timestamp);
        var descending = GetSortDescending(filters, defaultValue: true);

        var query = _db.Logs.AsNoTracking().AsQueryable();

        if (clientId.HasValue)
            query = query.Where(x => x.ClientId == clientId.Value);

        if (siteId.HasValue)
            query = query.Where(x => x.SiteId == siteId.Value);
        if (agentId.HasValue)
            query = query.Where(x => x.AgentId == agentId.Value);
        if (from.HasValue)
            query = query.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(x => x.CreatedAt <= to.Value);

        var orderedQuery = orderBy switch
        {
            LogsOrderBy.Level => descending
                ? query.OrderByDescending(x => x.Level).ThenByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.Level).ThenByDescending(x => x.CreatedAt),
            LogsOrderBy.Source => descending
                ? query.OrderByDescending(x => x.Source).ThenByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.Source).ThenByDescending(x => x.CreatedAt),
            LogsOrderBy.Type => descending
                ? query.OrderByDescending(x => x.Type).ThenByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.Type).ThenByDescending(x => x.CreatedAt),
            _ => descending
                ? query.OrderByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.CreatedAt)
        };

        var rowsRaw = await orderedQuery
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = x.Id,
                ["siteId"] = x.SiteId,
                ["agentId"] = x.AgentId,
                ["type"] = x.Type.ToString(),
                ["level"] = x.Level.ToString(),
                ["source"] = x.Source.ToString(),
                ["message"] = x.Message,
                ["createdAt"] = x.CreatedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "siteId", "agentId", "type", "level", "source", "message", "createdAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryConfigurationAuditAsync(JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");
        var changedBy = GetString(filters, "changedBy");
        var orderBy = GetEnum(filters, "orderBy", ConfigurationAuditOrderBy.Timestamp);
        var descending = GetSortDescending(filters, defaultValue: true);

        var query = _db.ConfigurationAudits.AsNoTracking().AsQueryable();

        if (from.HasValue)
            query = query.Where(x => x.ChangedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(x => x.ChangedAt <= to.Value);
        if (!string.IsNullOrWhiteSpace(changedBy))
            query = query.Where(x => x.ChangedBy == changedBy);

        var orderedQuery = orderBy switch
        {
            ConfigurationAuditOrderBy.EntityType => descending
                ? query.OrderByDescending(x => x.EntityType).ThenByDescending(x => x.ChangedAt)
                : query.OrderBy(x => x.EntityType).ThenByDescending(x => x.ChangedAt),
            ConfigurationAuditOrderBy.ChangedBy => descending
                ? query.OrderByDescending(x => x.ChangedBy).ThenByDescending(x => x.ChangedAt)
                : query.OrderBy(x => x.ChangedBy).ThenByDescending(x => x.ChangedAt),
            ConfigurationAuditOrderBy.FieldName => descending
                ? query.OrderByDescending(x => x.FieldName).ThenByDescending(x => x.ChangedAt)
                : query.OrderBy(x => x.FieldName).ThenByDescending(x => x.ChangedAt),
            _ => descending
                ? query.OrderByDescending(x => x.ChangedAt)
                : query.OrderBy(x => x.ChangedAt)
        };

        var rowsRaw = await orderedQuery
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["entityType"] = x.EntityType.ToString(),
                ["entityId"] = x.EntityId,
                ["fieldName"] = x.FieldName,
                ["oldValue"] = x.OldValue,
                ["newValue"] = x.NewValue,
                ["reason"] = x.Reason,
                ["changedBy"] = x.ChangedBy,
                ["changedAt"] = x.ChangedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["entityType", "entityId", "fieldName", "oldValue", "newValue", "reason", "changedBy", "changedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryTicketsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var workflowStateId = GetGuid(filters, "workflowStateId");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");
        var orderBy = GetEnum(filters, "orderBy", TicketsOrderBy.Timestamp);
        var descending = GetSortDescending(filters, defaultValue: true);

        var query = _db.Tickets.AsNoTracking().Where(x => x.DeletedAt == null);

        if (clientId.HasValue)
            query = query.Where(x => x.ClientId == clientId.Value);

        if (siteId.HasValue)
            query = query.Where(x => x.SiteId == siteId.Value);
        if (workflowStateId.HasValue)
            query = query.Where(x => x.WorkflowStateId == workflowStateId.Value);
        if (from.HasValue)
            query = query.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(x => x.CreatedAt <= to.Value);

        var orderedQuery = orderBy switch
        {
            TicketsOrderBy.Priority => descending
                ? query.OrderByDescending(x => x.Priority).ThenByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.Priority).ThenByDescending(x => x.CreatedAt),
            TicketsOrderBy.SlaBreached => descending
                ? query.OrderByDescending(x => x.SlaBreached).ThenByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.SlaBreached).ThenByDescending(x => x.CreatedAt),
            TicketsOrderBy.ClosedAt => descending
                ? query.OrderByDescending(x => x.ClosedAt).ThenByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.ClosedAt).ThenByDescending(x => x.CreatedAt),
            _ => descending
                ? query.OrderByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.CreatedAt)
        };

        var rowsRaw = await orderedQuery
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = x.Id,
                ["siteId"] = x.SiteId,
                ["agentId"] = x.AgentId,
                ["title"] = x.Title,
                ["priority"] = x.Priority.ToString(),
                ["workflowStateId"] = x.WorkflowStateId,
                ["slaExpiresAt"] = x.SlaExpiresAt,
                ["slaBreached"] = x.SlaBreached,
                ["createdAt"] = x.CreatedAt,
                ["closedAt"] = x.ClosedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "siteId", "agentId", "title", "priority", "workflowStateId", "slaExpiresAt", "slaBreached", "createdAt", "closedAt"],
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
            where !clientId.HasValue || st.ClientId == clientId.Value
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
            Columns = ["clientName", "siteName", "agentId", "agentHostname", "automaticLabels", "osName", "osVersion", "osBuild", "osArchitecture", "processor", "processorCores", "processorThreads", "processorArchitecture", "processorFrequencyGhz", "processorSocket", "processorTdpWatts", "totalMemoryGB", "totalMemoryBytes", "gpuModel", "gpuMemoryGB", "gpuDriverVersion", "totalDisksCount", "motherboardManufacturer", "motherboardModel", "biosVersion", "biosManufacturer", "biosDate", "biosSerialNumber", "inventorySchemaVersion", "inventoryCollectedAt", "collectedAt"],
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
            where inv.IsPresent && (!clientId.HasValue || st.ClientId == clientId.Value)
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

    private static JsonElement ParseFilters(string? filtersJson)
    {
        if (string.IsNullOrWhiteSpace(filtersJson))
            return default;

        using var doc = JsonDocument.Parse(filtersJson);
        return doc.RootElement.Clone();
    }

    private static int GetLimit(JsonElement filters)
    {
        var value = GetInt(filters, "limit");
        if (value is null)
            return DefaultLimit;

        return Math.Clamp(value.Value, 1, MaxLimit);
    }

    private static int? GetInt(JsonElement filters, string property)
    {
        if (filters.ValueKind != JsonValueKind.Object)
            return null;

        if (!filters.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static string? GetString(JsonElement filters, string property)
    {
        if (filters.ValueKind != JsonValueKind.Object)
            return null;

        if (!filters.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static Guid? GetGuid(JsonElement filters, string property)
    {
        var value = GetString(filters, property);
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    private static DateTime? GetDateTime(JsonElement filters, string property)
    {
        var value = GetString(filters, property);
        return DateTime.TryParse(value, out var dateTime) ? dateTime : null;
    }

    private static bool GetSortDescending(JsonElement filters, bool defaultValue)
    {
        var direction = GetString(filters, "orderDirection");
        if (string.IsNullOrWhiteSpace(direction))
            return defaultValue;

        return direction.Equals("desc", StringComparison.OrdinalIgnoreCase);
    }

    private static TEnum GetEnum<TEnum>(JsonElement filters, string property, TEnum defaultValue) where TEnum : struct, Enum
    {
        var value = GetString(filters, property);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var result) ? result : defaultValue;
    }

    private async Task<Dictionary<Guid, string>> GetAutomaticLabelsByAgentAsync(IEnumerable<Guid> agentIds, CancellationToken cancellationToken)
    {
        var uniqueAgentIds = agentIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (uniqueAgentIds.Count == 0)
            return new Dictionary<Guid, string>();

        var labels = await _db.AgentLabels
            .AsNoTracking()
            .Where(item => uniqueAgentIds.Contains(item.AgentId) && item.SourceType == AgentLabelSourceType.Automatic)
            .ToListAsync(cancellationToken);

        return labels
            .GroupBy(item => item.AgentId)
            .ToDictionary(
                group => group.Key,
                group => string.Join(", ", group
                    .Select(item => item.Label)
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)));
    }
}
