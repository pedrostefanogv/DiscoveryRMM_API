using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AutoTicketRuleExecutionRepository : IAutoTicketRuleExecutionRepository
{
    private readonly DiscoveryDbContext _db;

    public AutoTicketRuleExecutionRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AutoTicketRuleExecution> CreateAsync(AutoTicketRuleExecution execution)
    {
        execution.Id = execution.Id == Guid.Empty ? IdGenerator.NewId() : execution.Id;
        execution.EvaluatedAt = execution.EvaluatedAt == default ? DateTime.UtcNow : execution.EvaluatedAt;
        _db.AutoTicketRuleExecutions.Add(execution);
        await _db.SaveChangesAsync();
        return execution;
    }

    public async Task<IReadOnlyList<AutoTicketRuleExecution>> GetByMonitoringEventIdAsync(Guid monitoringEventId)
    {
        return await _db.AutoTicketRuleExecutions
            .AsNoTracking()
            .Where(execution => execution.MonitoringEventId == monitoringEventId)
            .OrderByDescending(execution => execution.EvaluatedAt)
            .ToListAsync();
    }

    public async Task<int> GetCreatedCountForClientAlertAsync(Guid clientId, string alertCode, DateTime sinceUtc)
    {
        var normalizedAlertCode = alertCode.Trim().ToLowerInvariant();

        return await (
                from execution in _db.AutoTicketRuleExecutions.AsNoTracking()
                join monitoringEvent in _db.AgentMonitoringEvents.AsNoTracking()
                    on execution.MonitoringEventId equals monitoringEvent.Id
                where execution.Decision == Core.Enums.AutoTicketDecision.Created
                      && execution.EvaluatedAt >= sinceUtc
                      && monitoringEvent.ClientId == clientId
                      && monitoringEvent.AlertCode.ToLower() == normalizedAlertCode
                select execution.Id)
            .CountAsync();
    }

    public async Task<Guid?> GetReusableOpenTicketIdAsync(
        Guid clientId,
        Guid agentId,
        string alertCode,
        Guid? departmentId = null,
        Guid? workflowProfileId = null,
        string? category = null)
    {
        var normalizedAlertCode = alertCode.Trim().ToLowerInvariant();
        var normalizedCategory = string.IsNullOrWhiteSpace(category)
            ? null
            : category.Trim().ToLowerInvariant();

        var query =
            from execution in _db.AutoTicketRuleExecutions.AsNoTracking()
            join monitoringEvent in _db.AgentMonitoringEvents.AsNoTracking()
                on execution.MonitoringEventId equals monitoringEvent.Id
            join ticket in _db.Tickets.AsNoTracking()
                on execution.CreatedTicketId equals ticket.Id
            where execution.Decision == Core.Enums.AutoTicketDecision.Created
                  && execution.CreatedTicketId.HasValue
                  && execution.AgentId == agentId
                  && monitoringEvent.ClientId == clientId
                  && monitoringEvent.AgentId == agentId
                  && monitoringEvent.AlertCode.ToLower() == normalizedAlertCode
                  && ticket.ClientId == clientId
                  && ticket.ClosedAt == null
                  && ticket.DeletedAt == null
            select new
            {
                execution.EvaluatedAt,
                TicketId = execution.CreatedTicketId,
                ticket.DepartmentId,
                ticket.WorkflowProfileId,
                ticket.Category
            };

        if (departmentId.HasValue)
            query = query.Where(item => item.DepartmentId == departmentId.Value);
        else
            query = query.Where(item => item.DepartmentId == null);

        if (workflowProfileId.HasValue)
            query = query.Where(item => item.WorkflowProfileId == workflowProfileId.Value);
        else
            query = query.Where(item => item.WorkflowProfileId == null);

        if (normalizedCategory is not null)
            query = query.Where(item => item.Category != null && item.Category.ToLower() == normalizedCategory);
        else
            query = query.Where(item => item.Category == null);

        return await query
            .OrderByDescending(item => item.EvaluatedAt)
            .Select(item => item.TicketId)
            .FirstOrDefaultAsync();
    }

    public async Task<Guid?> GetReopenableClosedTicketIdAsync(
        Guid clientId,
        Guid agentId,
        string alertCode,
        DateTime closedAfterUtc,
        Guid? departmentId = null,
        Guid? workflowProfileId = null,
        string? category = null)
    {
        var normalizedAlertCode = alertCode.Trim().ToLowerInvariant();
        var normalizedCategory = string.IsNullOrWhiteSpace(category)
            ? null
            : category.Trim().ToLowerInvariant();

        var query =
            from execution in _db.AutoTicketRuleExecutions.AsNoTracking()
            join monitoringEvent in _db.AgentMonitoringEvents.AsNoTracking()
                on execution.MonitoringEventId equals monitoringEvent.Id
            join ticket in _db.Tickets.AsNoTracking()
                on execution.CreatedTicketId equals ticket.Id
            where execution.Decision == Core.Enums.AutoTicketDecision.Created
                  && execution.CreatedTicketId.HasValue
                  && execution.AgentId == agentId
                  && monitoringEvent.ClientId == clientId
                  && monitoringEvent.AgentId == agentId
                  && monitoringEvent.AlertCode.ToLower() == normalizedAlertCode
                  && ticket.ClientId == clientId
                  && ticket.ClosedAt != null
                  && ticket.ClosedAt >= closedAfterUtc
                  && ticket.DeletedAt == null
            select new
            {
                execution.EvaluatedAt,
                TicketId = execution.CreatedTicketId,
                ticket.DepartmentId,
                ticket.WorkflowProfileId,
                ticket.Category,
                ticket.ClosedAt
            };

        if (departmentId.HasValue)
            query = query.Where(item => item.DepartmentId == departmentId.Value);
        else
            query = query.Where(item => item.DepartmentId == null);

        if (workflowProfileId.HasValue)
            query = query.Where(item => item.WorkflowProfileId == workflowProfileId.Value);
        else
            query = query.Where(item => item.WorkflowProfileId == null);

        if (normalizedCategory is not null)
            query = query.Where(item => item.Category != null && item.Category.ToLower() == normalizedCategory);
        else
            query = query.Where(item => item.Category == null);

        return await query
            .OrderByDescending(item => item.ClosedAt)
            .ThenByDescending(item => item.EvaluatedAt)
            .Select(item => item.TicketId)
            .FirstOrDefaultAsync();
    }

    public async Task<AutoTicketRuleStatsSnapshot> GetRuleStatsAsync(AutoTicketRule rule, DateTime periodStartUtc, DateTime periodEndUtc)
    {
        var scopedEvaluations = BuildScopedEvaluationsQuery(rule, periodStartUtc, periodEndUtc);
        var totalEvaluations = await scopedEvaluations.CountAsync();

        var selectedQuery = scopedEvaluations.Where(item => item.Execution.RuleId == rule.Id);
        var decisionCounts = await selectedQuery
            .GroupBy(item => item.Execution.Decision)
            .ToDictionaryAsync(group => group.Key, group => group.Count());

        var selectedExecutions = decisionCounts.Values.Sum();

        return new AutoTicketRuleStatsSnapshot
        {
            TotalEvaluations = totalEvaluations,
            SelectedExecutions = selectedExecutions,
            CreatedCount = GetDecisionCount(decisionCounts, Core.Enums.AutoTicketDecision.Created),
            DedupedCount = GetDecisionCount(decisionCounts, Core.Enums.AutoTicketDecision.Deduped),
            SuppressedCount = GetDecisionCount(decisionCounts, Core.Enums.AutoTicketDecision.Suppressed),
            MatchedNoActionCount = GetDecisionCount(decisionCounts, Core.Enums.AutoTicketDecision.MatchedNoAction),
            FailedCount = GetDecisionCount(decisionCounts, Core.Enums.AutoTicketDecision.Failed),
            RateLimitedCount = GetDecisionCount(decisionCounts, Core.Enums.AutoTicketDecision.RateLimited),
            FirstSelectedAtUtc = await selectedQuery
                .OrderBy(item => item.Execution.EvaluatedAt)
                .Select(item => (DateTime?)item.Execution.EvaluatedAt)
                .FirstOrDefaultAsync(),
            LastSelectedAtUtc = await selectedQuery
                .OrderByDescending(item => item.Execution.EvaluatedAt)
                .Select(item => (DateTime?)item.Execution.EvaluatedAt)
                .FirstOrDefaultAsync()
        };
    }

    private IQueryable<RuleExecutionStatsRow> BuildScopedEvaluationsQuery(AutoTicketRule rule, DateTime periodStartUtc, DateTime periodEndUtc)
    {
        var query =
            from execution in _db.AutoTicketRuleExecutions.AsNoTracking()
            join monitoringEvent in _db.AgentMonitoringEvents.AsNoTracking()
                on execution.MonitoringEventId equals monitoringEvent.Id
            where execution.EvaluatedAt >= periodStartUtc && execution.EvaluatedAt <= periodEndUtc
            select new RuleExecutionStatsRow
            {
                Execution = execution,
                MonitoringEvent = monitoringEvent
            };

        query = rule.ScopeLevel switch
        {
            Core.Enums.AutoTicketScopeLevel.Client when rule.ScopeId.HasValue => query.Where(item => item.MonitoringEvent.ClientId == rule.ScopeId.Value),
            Core.Enums.AutoTicketScopeLevel.Site when rule.ScopeId.HasValue => query.Where(item => item.MonitoringEvent.SiteId == rule.ScopeId.Value),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(rule.AlertCodeFilter))
        {
            var normalizedAlertCode = rule.AlertCodeFilter.Trim().ToLowerInvariant();
            query = query.Where(item => item.MonitoringEvent.AlertCode.ToLower() == normalizedAlertCode);
        }

        if (rule.SourceFilter.HasValue)
            query = query.Where(item => item.MonitoringEvent.Source == rule.SourceFilter.Value);

        if (rule.SeverityMin.HasValue)
            query = query.Where(item => item.MonitoringEvent.Severity >= rule.SeverityMin.Value);

        if (rule.SeverityMax.HasValue)
            query = query.Where(item => item.MonitoringEvent.Severity <= rule.SeverityMax.Value);

        return query;
    }

    private static int GetDecisionCount(IReadOnlyDictionary<Core.Enums.AutoTicketDecision, int> counts, Core.Enums.AutoTicketDecision decision)
        => counts.TryGetValue(decision, out var count) ? count : 0;

    private sealed class RuleExecutionStatsRow
    {
        public required AutoTicketRuleExecution Execution { get; init; }
        public required AgentMonitoringEvent MonitoringEvent { get; init; }
    }
}