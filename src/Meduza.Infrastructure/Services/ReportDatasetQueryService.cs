using System.Text.Json;
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
        var filters = ParseFilters(filtersJson);
        var clientId = GetGuid(filters, "clientId");

        return template.DatasetType switch
        {
            ReportDatasetType.SoftwareInventory => await QuerySoftwareInventoryAsync(clientId, filters, cancellationToken),
            ReportDatasetType.Logs => await QueryLogsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.ConfigurationAudit => await QueryConfigurationAuditAsync(filters, cancellationToken),
            ReportDatasetType.Tickets => await QueryTicketsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AgentHardware => await QueryAgentHardwareAsync(clientId, filters, cancellationToken),
            _ => new ReportQueryResult { Columns = ["message"], Rows = [new Dictionary<string, object?> { ["message"] = "Dataset not supported." }] }
        };
    }

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

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["clientName"] = x.ClientName,
                ["siteName"] = x.SiteName,
                ["agentId"] = x.AgentId,
                ["agentHostname"] = x.AgentHostname,
                ["softwareName"] = x.SoftwareName,
                ["publisher"] = x.Publisher,
                ["version"] = x.Version,
                ["lastSeenAt"] = x.LastSeenAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["clientName", "siteName", "agentId", "agentHostname", "softwareName", "publisher", "version", "lastSeenAt"],
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
            where !clientId.HasValue || st.ClientId == clientId.Value
            select new
            {
                SiteId = st.Id,
                SiteName = st.Name,
                AgentId = ag.Id,
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
                // Memória
                hw.TotalMemoryBytes,
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
                // Metadata
                hw.CollectedAt,
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

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["siteName"] = x.SiteName,
                ["agentHostname"] = x.AgentHostname,
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
                // Memory Info
                ["totalMemoryGB"] = x.TotalMemoryBytes.HasValue ? decimal.Round(x.TotalMemoryBytes.Value / 1024m / 1024m / 1024m, 2) : (object?)null,
                ["totalMemoryBytes"] = x.TotalMemoryBytes,
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
                // Metadata
                ["collectedAt"] = x.CollectedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["siteName", "agentHostname", "osName", "osVersion", "osBuild", "osArchitecture", "processor", "processorCores", "processorThreads", "processorArchitecture", "totalMemoryGB", "motherboardManufacturer", "motherboardModel", "biosVersion", "biosManufacturer", "collectedAt"],
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
}
