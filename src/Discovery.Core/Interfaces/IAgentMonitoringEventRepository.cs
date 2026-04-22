using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentMonitoringEventRepository
{
    Task<AgentMonitoringEvent?> GetByIdAsync(Guid id);
    Task<AgentMonitoringEvent> CreateAsync(AgentMonitoringEvent monitoringEvent);
}