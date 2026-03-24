namespace Meduza.Core.Helpers;

public static class NatsSubjectBuilder
{
    public static string AgentBase(Guid clientId, Guid siteId, Guid agentId)
        => $"tenant.{clientId}.site.{siteId}.agent.{agentId}";

    public static string AgentSubject(Guid clientId, Guid siteId, Guid agentId, string messageType)
        => $"{AgentBase(clientId, siteId, agentId)}.{messageType}";

    public static string AgentLegacySubject(Guid agentId, string messageType)
        => $"agent.{agentId}.{messageType}";

    public static string DashboardSubject(Guid? clientId, Guid? siteId)
    {
        if (clientId.HasValue && siteId.HasValue)
            return $"tenant.{clientId}.site.{siteId}.dashboard.events";
        if (clientId.HasValue)
            return $"tenant.{clientId}.dashboard.events";
        return "dashboard.events";
    }
}
