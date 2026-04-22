using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAutoTicketOrchestratorService
{
    Task<AutoTicketRuleExecution> EvaluateAsync(AgentMonitoringEvent monitoringEvent, CancellationToken cancellationToken = default);
}