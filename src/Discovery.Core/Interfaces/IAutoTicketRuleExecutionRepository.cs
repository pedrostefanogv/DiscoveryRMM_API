using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAutoTicketRuleExecutionRepository
{
    Task<AutoTicketRuleExecution> CreateAsync(AutoTicketRuleExecution execution);
    Task<IReadOnlyList<AutoTicketRuleExecution>> GetByMonitoringEventIdAsync(Guid monitoringEventId);
    Task<int> GetCreatedCountForClientAlertAsync(Guid clientId, string alertCode, DateTime sinceUtc);
    Task<Guid?> GetReusableOpenTicketIdAsync(
        Guid clientId,
        Guid agentId,
        string alertCode,
        Guid? departmentId = null,
        Guid? workflowProfileId = null,
        string? category = null);
    Task<Guid?> GetReopenableClosedTicketIdAsync(
        Guid clientId,
        Guid agentId,
        string alertCode,
        DateTime closedAfterUtc,
        Guid? departmentId = null,
        Guid? workflowProfileId = null,
        string? category = null);
    Task<AutoTicketRuleStatsSnapshot> GetRuleStatsAsync(AutoTicketRule rule, DateTime periodStartUtc, DateTime periodEndUtc);
}