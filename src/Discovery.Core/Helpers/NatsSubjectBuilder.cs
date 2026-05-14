namespace Discovery.Core.Helpers;

public static class NatsSubjectBuilder
{
    public const string SiteAgentsCommandStreamSubject = "tenant.*.site.*.agents.command";
    public const string ClientAgentsCommandStreamSubject = "tenant.*.agents.command";
    public const string GlobalAgentsCommandStreamSubject = "tenant.global.agents.command";
    public const string GlobalPongSubject = "tenant.global.pong";

    public static string AgentBase(Guid clientId, Guid siteId, Guid agentId)
        => $"tenant.{clientId}.site.{siteId}.agent.{agentId}";

    public static string AgentSubject(Guid clientId, Guid siteId, Guid agentId, string messageType)
        => $"{AgentBase(clientId, siteId, agentId)}.{messageType}";

    /// <summary>
    /// Subject de eventos para o dashboard.
    /// Com clientId+siteId: tenant.{c}.site.{s}.dashboard.events
    /// Com clientId apenas: tenant.{c}.dashboard.events
    /// Sem tenant (agentes orfaos): tenant.unscoped.dashboard.events (fallback global)
    /// </summary>
    public static string DashboardSubject(Guid? clientId, Guid? siteId)
    {
        if (clientId.HasValue && siteId.HasValue)
            return $"tenant.{clientId}.site.{siteId}.dashboard.events";
        if (clientId.HasValue)
            return $"tenant.{clientId}.dashboard.events";

        // Agents sem tenant: usa subject global de fallback.
        // O servidor escuta este subject e faz broadcast para dashboard via NATS WS.
        return "tenant.unscoped.dashboard.events";
    }

    /// <summary>
    /// Subject de comando fan-out por site.
    /// </summary>
    public static string SiteAgentsCommandSubject(Guid clientId, Guid siteId)
        => $"tenant.{clientId}.site.{siteId}.agents.command";

    /// <summary>
    /// Subject de comando fan-out por cliente.
    /// </summary>
    public static string ClientAgentsCommandSubject(Guid clientId)
        => $"tenant.{clientId}.agents.command";

    /// <summary>
    /// Subject de comando fan-out global.
    /// </summary>
    public static string GlobalAgentsCommandSubject()
        => GlobalAgentsCommandStreamSubject;

    /// <summary>
    /// Subject global de pong do servidor para os agents.
    /// </summary>
    public static string ServerPongSubject()
        => GlobalPongSubject;

    /// <summary>
    /// Subject de eventos P2P por cliente (peer.online, peer.offline).
    /// Agents assinam este subject para descobrir peers em tempo real.
    /// </summary>
    public static string P2pClientEventsSubject(Guid clientId)
        => $"tenant.{clientId}.p2p.events";
}
