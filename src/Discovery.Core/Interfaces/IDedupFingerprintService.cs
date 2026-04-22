using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IDedupFingerprintService
{
    string BuildDedupKey(AgentMonitoringEvent monitoringEvent, AutoTicketRule rule);
}