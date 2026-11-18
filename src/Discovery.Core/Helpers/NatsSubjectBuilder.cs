namespace Discovery.Core.Helpers;

public static class NatsSubjectBuilder
{
    public const string SiteAgentsCommandStreamSubject = "tenant.*.site.*.agents.command";
    public const string ClientAgentsCommandStreamSubject = "tenant.*.agents.command";
    public const string GlobalAgentsCommandStreamSubject = "tenant.global.agents.command";
    public const string GlobalPongSubject = "tenant.global.pong";

    public const string GlobalFanoutStreamName = "DISCOVERY_FANOUT_COMMANDS_GLOBAL";
    public const string LegacyFanoutStreamName = "DISCOVERY_FANOUT_COMMANDS";
    public const string LegacySupportChatStreamName = "DISCOVERY_SUPPORT_CHAT";

    public static string TenantFanoutStreamName(Guid clientId)
        => $"DISCOVERY_FANOUT_COMMANDS_{clientId:N}";

    public static string TenantSupportChatStreamName(Guid clientId)
        => $"DISCOVERY_SUPPORT_CHAT_{clientId:N}";

    public static string TenantSiteAgentsCommandStreamSubject(Guid clientId)
        => $"tenant.{clientId}.site.*.agents.command";

    public static string TenantClientAgentsCommandStreamSubject(Guid clientId)
        => $"tenant.{clientId}.agents.command";

    public static string TenantSupportChatStreamSubject(Guid clientId)
        => $"tenant.{clientId}.site.*.agent.*.supportchat.*";

    /// <summary>
    /// Faz o parse autoritativo do subject canônico de agente:
    /// <c>tenant.{clientId}.site.{siteId}.agent.{agentId}.{suffix...}</c>.
    /// Rejeita subjects fora do padrão ou com GUIDs vazios.
    /// </summary>
    public static bool TryParseAgentSubject(
        string? subject,
        out Guid clientId,
        out Guid siteId,
        out Guid agentId)
    {
        clientId = Guid.Empty;
        siteId = Guid.Empty;
        agentId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(subject) || subject.EndsWith('.'))
            return false;

        var parts = subject.Split('.');
        if (parts.Length < 7)
            return false;

        for (var i = 0; i < parts.Length; i++)
        {
            var seg = parts[i];
            if (string.IsNullOrWhiteSpace(seg) || seg == "*" || seg == ">")
                return false;
        }

        if (!string.Equals(parts[0], "tenant", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[2], "site", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[4], "agent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Guid.TryParse(parts[1], out clientId) || clientId == Guid.Empty)
            return false;
        if (!Guid.TryParse(parts[3], out siteId) || siteId == Guid.Empty)
            return false;
        if (!Guid.TryParse(parts[5], out agentId) || agentId == Guid.Empty)
            return false;

        return true;
    }

    /// <summary>
    /// Subject do stream JetStream do chat de suporte (captura as duas direções).
    /// Um ÚNICO wildcard: incluir variantes com overlap faz o NATS rejeitar o
    /// stream com "subject overlaps with" (ver NatsFanoutStreamBootstrapService).
    /// </summary>
    public const string SupportChatStreamSubject = "tenant.*.site.*.agent.*.supportchat.*";

    /// <summary>
    /// Subject (agent -> servidor) do chat de suporte. É o que a API assina
    /// para persistir as mensagens do usuário final.
    /// </summary>
    public const string SupportChatStreamAgentSubject = "tenant.*.site.*.agent.*.supportchat.agent";

    public static string AgentBase(Guid clientId, Guid siteId, Guid agentId)
        => $"tenant.{clientId}.site.{siteId}.agent.{agentId}";

    /// <summary>
    /// Subject (servidor -> agent) do chat de suporte: sessão aberta/encerrada e
    /// mensagens do atendente. O agent assina com consumer JetStream durável
    /// (replay sem perda após reconexão/restart).
    /// </summary>
    public static string SupportChatServerSubject(Guid clientId, Guid siteId, Guid agentId)
        => AgentSubject(clientId, siteId, agentId, "supportchat.server");

    /// <summary>
    /// Subject (agent -> servidor) do chat de suporte: mensagens do usuário
    /// final e resposta ao convite.
    /// </summary>
    public static string SupportChatAgentSubject(Guid clientId, Guid siteId, Guid agentId)
        => AgentSubject(clientId, siteId, agentId, "supportchat.agent");

    public static string AgentSubject(Guid clientId, Guid siteId, Guid agentId, string messageType)
        => $"{AgentBase(clientId, siteId, agentId)}.{messageType}";

    /// <summary>
    /// Subject do stream de logs do debug remoto (agent -> viewer).
    /// </summary>
    public static string RemoteDebugLogSubject(Guid clientId, Guid siteId, Guid agentId)
        => AgentSubject(clientId, siteId, agentId, "remote-debug.log");

    /// <summary>
    /// Subject UNICO de controle do debug remoto. O MESMO subject carrega
    /// ping/pong (liveness) e setLevel; o agente publica E assina nele.
    /// </summary>
    public static string RemoteDebugControlSubject(Guid clientId, Guid siteId, Guid agentId)
        => AgentSubject(clientId, siteId, agentId, "remote-debug.control");

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

    /// <summary>
    /// Subject de descoberta P2P por site — DESCONTINUADO desde v4.4.0.
    /// Migrado para P2pClientEventsSubject (tenant.{clientId}.p2p.events).
    /// Nao utilizar em novas implementacoes.
    /// </summary>
    [Obsolete("Migrado para P2pClientEventsSubject. Nao utilizar em novas implementacoes.")]
    public static string P2pSiteDiscoverySubject(Guid clientId, Guid siteId)
        => $"tenant.{clientId}.site.{siteId}.p2p.discovery";
}
