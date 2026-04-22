using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

public interface IAutoTicketRuleEngineService
{
    Task<AutoTicketRuleDecision> EvaluateAsync(
        AgentMonitoringEvent monitoringEvent,
        IReadOnlyCollection<string> labels,
        IReadOnlyCollection<AutoTicketRule>? candidateRules = null,
        CancellationToken cancellationToken = default);
}