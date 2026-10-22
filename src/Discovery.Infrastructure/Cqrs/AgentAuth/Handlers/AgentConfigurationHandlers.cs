using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentConfigurationHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IConfigurationService configService,
    IAgentTlsCertificateProbe tlsProbe
) : IRequestHandler<GetAgentConfigurationQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentConfigurationQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var serverConfig = await configService.GetServerConfigAsync();

        ClientConfiguration? clientConfig = null;
        SiteConfiguration? siteConfig = null;
        if (site is not null)
        {
            clientConfig = await configService.GetClientConfigAsync(site.ClientId);
            siteConfig = await configService.GetSiteConfigAsync(agent.SiteId);
        }

        // Resolve effective configuration (Site > Client > Server)
        // Sondas TLS em paralelo (fora do inicializador): cada uma tem timeout de 10s
        // no cache miss e este handler roda no provisionamento/refresh do agent —
        // sequenciais dobrariam o pior caso de latência do endpoint.
        var apiProbeTask = tlsProbe.GetExpectedTlsCertHashAsync(ct);
        var natsProbeTask = tlsProbe.GetExpectedNatsTlsCertHashAsync(ct);

        var effective = new Dictionary<string, object?>
        {
            ["agentId"] = agent.Id.ToString(),
            ["clientId"] = site?.ClientId.ToString(),
            ["siteId"] = agent.SiteId.ToString(),

            // Feature flags (resolved hierarchy)
            ["recoveryEnabled"] = siteConfig?.RecoveryEnabled ?? clientConfig?.RecoveryEnabled ?? serverConfig.RecoveryEnabled,
            ["discoveryEnabled"] = siteConfig?.DiscoveryEnabled ?? clientConfig?.DiscoveryEnabled ?? serverConfig.DiscoveryEnabled,
            ["supportEnabled"] = siteConfig?.SupportEnabled ?? clientConfig?.SupportEnabled ?? serverConfig.SupportEnabled,
            ["knowledgeBaseEnabled"] = siteConfig?.KnowledgeBaseEnabled ?? clientConfig?.KnowledgeBaseEnabled ?? serverConfig.KnowledgeBaseEnabled,
            ["chatAIEnabled"] = siteConfig?.ChatAIEnabled ?? clientConfig?.ChatAIEnabled ?? serverConfig.ChatAIEnabled,

            // Página inicial do agent (herdada; vazio cai para o default "status")
            ["agentHomeTab"] = AgentHomeTabCatalog.Normalize(
                FirstNonEmpty(siteConfig?.AgentHomeTab, clientConfig?.AgentHomeTab, serverConfig.AgentHomeTab)),
            ["p2pFilesEnabled"] = siteConfig?.P2PFilesEnabled ?? clientConfig?.P2PFilesEnabled ?? serverConfig.P2PFilesEnabled,
            ["cloudBootstrapEnabled"] = clientConfig?.CloudBootstrapEnabled ?? serverConfig.CloudBootstrapEnabled,
            ["zeroTouchEnabled"] = siteConfig?.ZeroTouchEnabled ?? clientConfig?.ZeroTouchEnabled ?? serverConfig.ZeroTouchEnabled,

            // App store
            ["appStoreEnabled"] = serverConfig.AppStorePolicy != Core.Enums.AppStorePolicyType.Disabled,
            ["appStorePolicy"] = (siteConfig?.AppStorePolicy ?? clientConfig?.AppStorePolicy ?? serverConfig.AppStorePolicy).ToString(),

            // NATS configuration
            ["natsEnabled"] = serverConfig.NatsEnabled,
            ["natsServerHost"] = serverConfig.NatsServerHostExternal,
            ["natsServerHostInternal"] = serverConfig.NatsServerHostInternal,
            ["natsUseWssExternal"] = serverConfig.NatsUseWssExternal,
            ["natsAuthEnabled"] = serverConfig.NatsAuthEnabled,

            // Heartbeat
            ["heartbeatIntervalSeconds"] = serverConfig.AgentHeartbeatIntervalSeconds,
            ["onlineGraceSeconds"] = serverConfig.AgentOnlineGraceSeconds,

            // Inventory
            ["inventoryIntervalHours"] = siteConfig?.InventoryIntervalHours ?? clientConfig?.InventoryIntervalHours ?? serverConfig.InventoryIntervalHours,

            // Gerenciador de patches (Windows Update): kill switch e cadência.
            ["windowsUpdateEnabled"] = siteConfig?.WindowsUpdateEnabled ?? clientConfig?.WindowsUpdateEnabled ?? serverConfig.WindowsUpdateEnabled,
            ["windowsUpdateScanIntervalMinutes"] = siteConfig?.WindowsUpdateScanIntervalMinutes ?? clientConfig?.WindowsUpdateScanIntervalMinutes ?? serverConfig.WindowsUpdateScanIntervalMinutes,

            // Autorização de acesso remoto (informativo para diagnóstico; a
            // política efetiva por usuário é resolvida e enviada no start).
            ["remoteConsent"] = new
            {
                mode = (siteConfig?.RemoteConsentMode ?? clientConfig?.RemoteConsentMode ?? serverConfig.RemoteConsentMode).ToString(),
                timeoutSeconds = siteConfig?.RemoteConsentTimeoutSeconds ?? clientConfig?.RemoteConsentTimeoutSeconds ?? serverConfig.RemoteConsentTimeoutSeconds,
                timeoutAction = (siteConfig?.RemoteConsentTimeoutAction ?? clientConfig?.RemoteConsentTimeoutAction ?? serverConfig.RemoteConsentTimeoutAction).ToString(),
                showBanner = siteConfig?.RemoteConsentShowBanner ?? clientConfig?.RemoteConsentShowBanner ?? serverConfig.RemoteConsentShowBanner
            },

            // Update policies (JSON strings)
            ["agentUpdatePolicy"] = TryDeserializeJson(clientConfig?.AgentUpdatePolicyJson ?? serverConfig.AgentUpdatePolicyJson),
            // Janela de manutenção (reboot/patches) e política de tools MCP do chat:
            // cascata site > client > server; null/vazio herda. O agent tolera ausência
            // (defaults: janela off, mode all — compatibilidade com frota existente).
            ["maintenanceWindow"] = TryDeserializeJson(siteConfig?.MaintenanceWindowJson ?? clientConfig?.MaintenanceWindowJson ?? serverConfig.MaintenanceWindowJson),
            ["mcpToolPolicy"] = TryDeserializeJson(siteConfig?.McpToolPolicyJson ?? clientConfig?.McpToolPolicyJson ?? serverConfig.McpToolPolicyJson),

            // Branding & notification
            ["brandingSettings"] = TryDeserializeJson(serverConfig.BrandingSettingsJson),

            // PSADT settings (from server config)
            ["psadtEnabled"] = false,
            ["psadtSettings"] = (object?)null,

            // Consolidation & rollout settings
            ["consolidationSettings"] = (object?)null,
            ["rolloutSettings"] = (object?)null,

            // AI integration
            ["aiIntegrationSettings"] = TryDeserializeJson(siteConfig?.AIIntegrationSettingsJson ?? clientConfig?.AIIntegrationSettingsJson ?? serverConfig.AIIntegrationSettingsJson),

            // Retention & reporting
            ["reportingSettings"] = TryDeserializeJson(serverConfig.ReportingSettingsJson),
            ["retentionSettings"] = TryDeserializeJson(serverConfig.RetentionSettingsJson),

            // TLS cert hashes (computed at runtime by the server)
            // C2 (pin de TLS no agent): a sonda observa o certificado publicado no
            // endpoint público (tipicamente nginx/Cloudflare) e publica o SHA-256.
            // Hash ausente mantém null — o agent só impõe o pin com enforce=true
            // COM hash publicado; enforce sem hash é fail-closed (tlsutil.PinnedTLSConfig).
                        ["apiTlsCertHash"] = await apiProbeTask,
            ["natsTlsCertHash"] = await natsProbeTask,
        };

        return Result<object>.Success(effective);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static object? TryDeserializeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return null;
        try { return JsonSerializer.Deserialize<object>(json); }
        catch { return json; }
    }
}

public sealed class ReportAgentTlsMismatchHandler(
    ILogger<ReportAgentTlsMismatchHandler> logger
) : IRequestHandler<ReportAgentTlsMismatchCommand, Result<object>>
{
    public Task<Result<object>> Handle(ReportAgentTlsMismatchCommand cmd, CancellationToken ct)
    {
        logger.LogWarning(
            "Agent reported TLS mismatch for target '{Target}'",
            cmd.Target);

        return Task.FromResult(Result<object>.Success(new { reported = true, target = cmd.Target }));
    }
}

public sealed class GetAgentSyncManifestHandler(
    IAgentRepository agentRepo
) : IRequestHandler<GetAgentSyncManifestQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentSyncManifestQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var now = DateTime.UtcNow;
        var resources = new List<AgentSyncManifestResourceDto>
        {
            new()
            {
                Resource = Core.Enums.SyncResourceType.Configuration,
                Revision = now.ToString("yyyyMMddHHmmss"),
                RecommendedSyncInSeconds = 300, // 5 min
                Endpoint = "/api/v1/agent-auth/me/configuration"
            },
            new()
            {
                Resource = Core.Enums.SyncResourceType.AppStore,
                Revision = now.ToString("yyyyMMddHHmmss"),
                RecommendedSyncInSeconds = 3600, // 1 hour
                Endpoint = "/api/v1/agent-auth/me/app-store/effective"
            },
            new()
            {
                Resource = Core.Enums.SyncResourceType.AutomationPolicy,
                Revision = now.ToString("yyyyMMddHHmmss"),
                RecommendedSyncInSeconds = 600, // 10 min
                Endpoint = "/api/v1/agent-auth/me/automation/policy-sync"
            },
            new()
            {
                Resource = Core.Enums.SyncResourceType.AgentUpdate,
                Revision = now.ToString("yyyyMMddHHmmss"),
                RecommendedSyncInSeconds = 3600, // 1 hour
                Endpoint = "/api/v1/agent-auth/me/update/manifest"
            },
            new()
            {
                Resource = Core.Enums.SyncResourceType.ZeroTouchApproved,
                Revision = now.ToString("yyyyMMddHHmmss"),
                RecommendedSyncInSeconds = 300, // 5 min
                Endpoint = "/api/v1/agent-auth/me"
            }
        };

        var manifest = new AgentSyncManifestDto
        {
            GeneratedAtUtc = now,
            RecommendedPollSeconds = 60,
            MaxStaleSeconds = 300,
            Resources = resources
        };

        return Result<object>.Success(manifest);
    }
}