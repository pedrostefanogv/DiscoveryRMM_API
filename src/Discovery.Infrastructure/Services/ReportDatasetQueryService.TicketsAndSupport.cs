using System.Text.Json;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

public partial class ReportDatasetQueryService
{
    private async Task<ReportQueryResult> QueryTicketsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var workflowStateId = GetGuid(filters, "workflowStateId");
        var templateId = GetGuid(filters, "templateId");
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
        if (templateId.HasValue)
            query = query.Where(x => x.TemplateId == templateId.Value);
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
                ["closedAt"] = x.ClosedAt,
                ["templateId"] = x.TemplateId,
                ["templateName"] = x.TemplateName
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "siteId", "agentId", "title", "priority", "workflowStateId", "slaExpiresAt", "slaBreached", "createdAt", "closedAt", "templateId", "templateName"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryTicketActivityAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var ticketId = GetGuid(filters, "ticketId");
        var action = GetString(filters, "action");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");
        var descending = GetSortDescending(filters, defaultValue: true);

        var query = _db.TicketActivityLogs.AsNoTracking().AsQueryable();
        if (clientId.HasValue) query = query.Where(x => x.ClientId == clientId.Value);
        if (siteId.HasValue)
        {
            var siteTicketIds = _db.Tickets.AsNoTracking()
                .Where(t => t.DeletedAt == null && t.SiteId == siteId.Value)
                .Select(t => t.Id);
            query = query.Where(x => siteTicketIds.Contains(x.TicketId));
        }
        if (ticketId.HasValue) query = query.Where(x => x.TicketId == ticketId.Value);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action);
        if (from.HasValue) query = query.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.CreatedAt <= to.Value);

        query = descending
            ? query.OrderByDescending(x => x.CreatedAt)
            : query.OrderBy(x => x.CreatedAt);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["id"] = x.Id, ["ticketId"] = x.TicketId, ["clientId"] = x.ClientId,
            ["action"] = x.Action, ["changedBy"] = x.ChangedBy,
            ["oldValue"] = x.OldValue, ["newValue"] = x.NewValue,
            ["createdAt"] = x.CreatedAt
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "ticketId", "clientId", "action", "changedBy", "oldValue", "newValue", "createdAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryTicketEscalationsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var descending = GetSortDescending(filters, defaultValue: false);

        var query = _db.TicketEscalationRules.AsNoTracking().AsQueryable();
        if (clientId.HasValue) query = query.Where(x => x.ClientId == clientId.Value);

        query = descending
            ? query.OrderByDescending(x => x.Name)
            : query.OrderBy(x => x.Name);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["id"] = x.Id, ["clientId"] = x.ClientId, ["name"] = x.Name,
            ["escalationLevel"] = x.EscalationLevel, ["isActive"] = x.IsActive,
            ["createdAt"] = x.CreatedAt, ["updatedAt"] = x.UpdatedAt
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "clientId", "name", "escalationLevel", "isActive", "createdAt", "updatedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryTicketAnswersAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var templateId = GetGuid(filters, "templateId");
        var questionKey = GetString(filters, "questionKey");
        var valueText = GetString(filters, "valueText");
        var descending = GetSortDescending(filters, defaultValue: true);

        var tickets = _db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .Select(t => new { t.Id, t.Title, t.ClientId, t.SiteId, t.AgentId, t.TemplateName });

        var joined =
            from answer in _db.TicketAnswers.AsNoTracking()
            join ticket in tickets on answer.TicketId equals ticket.Id
            select new { answer, ticket };

        if (clientId.HasValue) joined = joined.Where(x => x.ticket.ClientId == clientId.Value);
        if (siteId.HasValue) joined = joined.Where(x => x.ticket.SiteId == siteId.Value);
        if (templateId.HasValue) joined = joined.Where(x => x.answer.TemplateId == templateId.Value);
        if (!string.IsNullOrWhiteSpace(questionKey)) joined = joined.Where(x => x.answer.QuestionKey == questionKey);
        if (!string.IsNullOrWhiteSpace(valueText)) joined = joined.Where(x => x.answer.ValueText == valueText);

        joined = descending
            ? joined.OrderByDescending(x => x.answer.CreatedAt)
            : joined.OrderBy(x => x.answer.CreatedAt);

        var rowsRaw = await joined.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["ticketId"] = x.ticket.Id,
            ["ticketTitle"] = x.ticket.Title,
            ["clientId"] = x.ticket.ClientId,
            ["siteId"] = x.ticket.SiteId,
            ["agentId"] = x.ticket.AgentId,
            ["templateId"] = x.answer.TemplateId,
            ["templateName"] = x.ticket.TemplateName,
            ["questionKey"] = x.answer.QuestionKey,
            ["questionLabel"] = x.answer.QuestionLabel,
            ["valueText"] = x.answer.ValueText,
            ["createdAt"] = x.answer.CreatedAt
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["ticketId", "ticketTitle", "clientId", "siteId", "agentId", "templateId", "templateName", "questionKey", "questionLabel", "valueText", "createdAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryCustomFieldsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var entityName = GetString(filters, "entityName");
        var descending = GetSortDescending(filters, defaultValue: false);

        var query = _db.CustomFieldDefinitions.AsNoTracking().AsQueryable();
        if (clientId.HasValue) query = query.Where(x => x.ClientId == clientId.Value);
        if (!string.IsNullOrWhiteSpace(entityName)) query = query.Where(x => x.EntityName == entityName);

        query = descending
            ? query.OrderByDescending(x => x.Name)
            : query.OrderBy(x => x.EntityName).ThenBy(x => x.Name);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["id"] = x.Id, ["clientId"] = x.ClientId, ["entityName"] = x.EntityName,
            ["fieldName"] = x.Name, ["valueType"] = x.ValueType.ToString(),
            ["isRequired"] = x.IsRequired, ["isActive"] = x.IsActive,
            ["createdAt"] = x.CreatedAt, ["updatedAt"] = x.UpdatedAt
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "clientId", "entityName", "fieldName", "valueType", "isRequired", "isActive", "createdAt", "updatedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryKnowledgeBaseAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var status = GetString(filters, "status");
        var category = GetString(filters, "category");
        var descending = GetSortDescending(filters, defaultValue: true);

        var query = _db.KnowledgeArticles.AsNoTracking().AsQueryable();
        if (clientId.HasValue) query = query.Where(x => x.ClientId == clientId.Value);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim().ToLowerInvariant();
            query = query.Where(x => x.Status.ToLower() == normalizedStatus);
        }
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category);

        query = descending
            ? query.OrderByDescending(x => x.UpdatedAt)
            : query.OrderBy(x => x.Title);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["id"] = x.Id, ["clientId"] = x.ClientId, ["title"] = x.Title,
            ["category"] = x.Category, ["status"] = x.Status.ToString(),
            ["author"] = x.Author, ["updatedAt"] = x.UpdatedAt,
            ["createdAt"] = x.CreatedAt
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "clientId", "title", "category", "status", "author", "updatedAt", "createdAt"],
            Rows = rows
        };
    }
}
