using System.Text.Json;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

public partial class ReportDatasetQueryService
{
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

    private async Task<ReportQueryResult> QueryConfigurationAuditAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");
        var changedBy = GetString(filters, "changedBy");
        var orderBy = GetEnum(filters, "orderBy", ConfigurationAuditOrderBy.Timestamp);
        var descending = GetSortDescending(filters, defaultValue: true);

        var query = _db.ConfigurationAudits.AsNoTracking().AsQueryable();

        if (clientId.HasValue)
        {
            var clientConfigIds = _db.ClientConfigurations
                .AsNoTracking()
                .Where(c => c.ClientId == clientId.Value)
                .Select(c => c.Id);

            var siteConfigIds =
                from sc in _db.SiteConfigurations.AsNoTracking()
                join st in _db.Sites.AsNoTracking() on sc.SiteId equals st.Id
                where st.ClientId == clientId.Value && (!siteId.HasValue || st.Id == siteId.Value)
                select sc.Id;

            var escalationIds = _db.TicketEscalationRules
                .AsNoTracking()
                .Where(r => r.ClientId == clientId.Value)
                .Select(r => r.Id);

            query = query.Where(x =>
                (x.EntityType == ConfigurationEntityType.Client && !siteId.HasValue && clientConfigIds.Contains(x.EntityId)) ||
                (x.EntityType == ConfigurationEntityType.Site && siteConfigIds.Contains(x.EntityId)) ||
                (x.EntityType == ConfigurationEntityType.TicketEscalationRule && !siteId.HasValue && escalationIds.Contains(x.EntityId)));
        }
        else if (siteId.HasValue)
        {
            var siteConfigIds = _db.SiteConfigurations
                .AsNoTracking()
                .Where(sc => sc.SiteId == siteId.Value)
                .Select(sc => sc.Id);

            query = query.Where(x => x.EntityType == ConfigurationEntityType.Site && siteConfigIds.Contains(x.EntityId));
        }

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

    private async Task<ReportQueryResult> QueryAutomationExecutionsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var taskId = GetGuid(filters, "taskId");
        var scriptId = GetGuid(filters, "scriptId");
        var orderBy = GetEnum(filters, "orderBy", AutomationExecutionsOrderBy.Timestamp);
        var descending = GetSortDescending(filters, defaultValue: true);

        var query =
            from aer in _db.AutomationExecutionReports.AsNoTracking()
            join ag in _db.Agents.AsNoTracking() on aer.AgentId equals ag.Id
            join st in _db.Sites.AsNoTracking() on ag.SiteId equals st.Id
            where !clientId.HasValue || st.ClientId == clientId.Value
            select new
            {
                aer.Id,
                aer.CommandId,
                AgentId = ag.Id,
                AgentHostname = ag.Hostname,
                SiteId = st.Id,
                SiteName = st.Name,
                aer.TaskId,
                aer.ScriptId,
                aer.SourceType,
                aer.Status,
                aer.ExitCode,
                aer.ErrorMessage,
                aer.CreatedAt,
                aer.ResultReceivedAt
            };

        if (siteId.HasValue)
            query = query.Where(x => x.SiteId == siteId.Value);
        if (agentId.HasValue)
            query = query.Where(x => x.AgentId == agentId.Value);
        if (taskId.HasValue)
            query = query.Where(x => x.TaskId == taskId.Value);
        if (scriptId.HasValue)
            query = query.Where(x => x.ScriptId == scriptId.Value);

        var orderedQuery = orderBy switch
        {
            AutomationExecutionsOrderBy.AgentHostname => descending
                ? query.OrderByDescending(x => x.AgentHostname).ThenByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.AgentHostname).ThenByDescending(x => x.CreatedAt),
            AutomationExecutionsOrderBy.Status => descending
                ? query.OrderByDescending(x => x.Status).ThenByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.Status).ThenByDescending(x => x.CreatedAt),
            AutomationExecutionsOrderBy.ExitCode => descending
                ? query.OrderByDescending(x => x.ExitCode).ThenByDescending(x => x.CreatedAt)
                : query.OrderBy(x => x.ExitCode).ThenByDescending(x => x.CreatedAt),
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
                ["executionId"] = x.Id,
                ["commandId"] = x.CommandId,
                ["agentId"] = x.AgentId,
                ["agentHostname"] = x.AgentHostname,
                ["siteId"] = x.SiteId,
                ["siteName"] = x.SiteName,
                ["taskId"] = x.TaskId,
                ["scriptId"] = x.ScriptId,
                ["sourceType"] = x.SourceType.ToString(),
                ["status"] = x.Status.ToString(),
                ["exitCode"] = x.ExitCode,
                ["errorMessage"] = x.ErrorMessage,
                ["createdAt"] = x.CreatedAt,
                ["completedAt"] = x.ResultReceivedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["executionId", "commandId", "agentId", "agentHostname", "siteId", "siteName", "taskId", "scriptId", "sourceType", "status", "exitCode", "errorMessage", "createdAt", "completedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryAutomationScriptsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var language = GetString(filters, "language");
        var descending = GetSortDescending(filters, defaultValue: false);

        var query = _db.AutomationScriptDefinitions.AsNoTracking().AsQueryable();
        if (clientId.HasValue) query = query.Where(x => x.ClientId == clientId.Value);
        if (!string.IsNullOrWhiteSpace(language)) query = query.Where(x => x.Language == language);

        query = descending
            ? query.OrderByDescending(x => x.UpdatedAt)
            : query.OrderBy(x => x.Name);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["id"] = x.Id, ["clientId"] = x.ClientId, ["name"] = x.Name,
            ["language"] = x.Language, ["isActive"] = x.IsActive,
            ["createdAt"] = x.CreatedAt, ["updatedAt"] = x.UpdatedAt
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "clientId", "name", "language", "isActive", "createdAt", "updatedAt"],
            Rows = rows
        };
    }
}
