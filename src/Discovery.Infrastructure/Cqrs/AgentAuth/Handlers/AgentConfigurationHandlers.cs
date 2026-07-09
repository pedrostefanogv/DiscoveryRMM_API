using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentConfigurationHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IConfigurationService configService
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
            ["p2pFilesEnabled"] = siteConfig?.P2PFilesEnabled ?? clientConfig?.P2PFilesEnabled ?? serverConfig.P2PFilesEnabled,
            ["cloudBootstrapEnabled"] = clientConfig?.CloudBootstrapEnabled ?? serverConfig.CloudBootstrapEnabled,

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

            // Update policies (JSON strings)
            ["autoUpdateSettings"] = TryDeserializeJson(siteConfig?.AutoUpdateSettingsJson ?? clientConfig?.AutoUpdateSettingsJson ?? serverConfig.AutoUpdateSettingsJson),
            ["agentUpdatePolicy"] = TryDeserializeJson(clientConfig?.AgentUpdatePolicyJson ?? serverConfig.AgentUpdatePolicyJson),

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
            ["apiTlsCertHash"] = (string?)null,   // TODO: compute from server certificate
            ["natsTlsCertHash"] = (string?)null,  // TODO: compute from NATS certificate

            // MeshCentral
            ["meshCentralEnabled"] = false,
            ["meshCentralGroupPolicyProfile"] = siteConfig?.MeshCentralGroupPolicyProfile ?? clientConfig?.MeshCentralGroupPolicyProfile ?? serverConfig.MeshCentralGroupPolicyProfile
        };

        return Result<object>.Success(effective);
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