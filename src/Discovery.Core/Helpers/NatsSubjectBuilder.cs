namespace Discovery.Core.Helpers;

public static class NatsSubjectBuilder
{
    public static string AgentBase(Guid clientId, Guid siteId, Guid agentId)
        => $"tenant.{clientId}.site.{siteId}.agent.{agentId}";

    public static string AgentSubject(Guid clientId, Guid siteId, Guid agentId, string messageType)
        => $"{AgentBase(clientId, siteId, agentId)}.{messageType}";

    public static string DashboardSubject(Guid? clientId, Guid? siteId)
    {
        if (clientId.HasValue && siteId.HasValue)
            return $"tenant.{clientId}.site.{siteId}.dashboard.events";
        if (clientId.HasValue)
            return $"tenant.{clientId}.dashboard.events";

        throw new InvalidOperationException("Dashboard subject requires at least client scope.");
    }

    /// <summary>
    /// Subject de descoberta P2P por site — todos os agents do site assinam.
    /// Apenas o servidor publica neste subject.
    /// </summary>
    public static string P2pSiteDiscoverySubject(Guid clientId, Guid siteId)
        => $"tenant.{clientId}.site.{siteId}.p2p.discovery";
}
