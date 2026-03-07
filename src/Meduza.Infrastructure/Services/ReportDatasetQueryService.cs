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

        var rowsRaw = await query
            .OrderBy(x => x.SoftwareName)
            .ThenBy(x => x.AgentHostname)
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

        var rowsRaw = await query
            .OrderByDescending(x => x.CreatedAt)
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

        var query = _db.ConfigurationAudits.AsNoTracking().AsQueryable();

        if (from.HasValue)
            query = query.Where(x => x.ChangedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(x => x.ChangedAt <= to.Value);
        if (!string.IsNullOrWhiteSpace(changedBy))
            query = query.Where(x => x.ChangedBy == changedBy);

        var rowsRaw = await query
            .OrderByDescending(x => x.ChangedAt)
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

        var rowsRaw = await query
            .OrderByDescending(x => x.CreatedAt)
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
                hw.OsName,
                hw.OsVersion,
                hw.Processor,
                hw.TotalMemoryBytes,
                hw.CollectedAt
            };

        if (siteId.HasValue)
            query = query.Where(x => x.SiteId == siteId.Value);
        if (agentId.HasValue)
            query = query.Where(x => x.AgentId == agentId.Value);

        var rowsRaw = await query
            .OrderBy(x => x.SiteName)
            .ThenBy(x => x.AgentHostname)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["siteId"] = x.SiteId,
                ["siteName"] = x.SiteName,
                ["agentId"] = x.AgentId,
                ["agentHostname"] = x.AgentHostname,
                ["osName"] = x.OsName,
                ["osVersion"] = x.OsVersion,
                ["processor"] = x.Processor,
                ["totalMemoryBytes"] = x.TotalMemoryBytes,
                ["collectedAt"] = x.CollectedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["siteId", "siteName", "agentId", "agentHostname", "osName", "osVersion", "processor", "totalMemoryBytes", "collectedAt"],
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
}
