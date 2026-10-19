using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentMonitoringEventRepository
{
    Task<AgentMonitoringEvent?> GetByIdAsync(Guid id);
    Task<AgentMonitoringEvent> CreateAsync(AgentMonitoringEvent monitoringEvent);

    /// <summary>
    /// Lista eventos de monitoramento por escopo (agente/cliente/site), com
    /// paginação por cursor (CreatedAt + Id, ordem decrescente). Usado pela
    /// query ListMonitoringEvents (endpoint /monitoring-events e tool MCP
    /// get_agent_metrics_history).
    /// </summary>
    Task<IReadOnlyList<AgentMonitoringEvent>> GetByFiltersPageAsync(
        Guid? agentId,
        Guid? clientId,
        Guid? siteId,
        string? cursor,
        int limit,
        CancellationToken ct = default);
}