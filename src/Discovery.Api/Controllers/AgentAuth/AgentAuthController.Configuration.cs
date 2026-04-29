using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent configuration, sync manifest, and TLS mismatch endpoints.
/// </summary>
public partial class AgentAuthController
{
    /// <summary>
    /// Returns effective agent configuration (hierarchical resolve: Server → Client → Site).
    /// </summary>
    [HttpGet("me/configuration")]
    public async Task<IActionResult> GetConfiguration()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: true);
        if (blocked is not null)
            return blocked;

        if (agent!.ZeroTouchPending)
            return Ok(new { zeroTouchPending = true });

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.SupportEnabled;
        if (resolved.AIIntegration is not null)
            resolved.AIIntegration.ApiKey = null;

        var serverConfig = await _configService.GetServerConfigAsync();

        var enforceTls = _configuration.GetValue<bool>("Security:AgentConnection:EnforceTlsHashValidation");
        var handshakeEnabled = _configuration.GetValue<bool>("Security:AgentConnection:HandshakeEnabled");

        string? apiTlsCertHash = null;
        if (enforceTls || handshakeEnabled)
            apiTlsCertHash = await _tlsCertProbe.GetExpectedTlsCertHashAsync(HttpContext.RequestAborted);

        string? natsTlsCertHash = null;
        var natsHost = string.IsNullOrWhiteSpace(serverConfig.NatsServerHostExternal)
            ? serverConfig.NatsServerHostInternal
            : serverConfig.NatsServerHostExternal;
        var natsProbeUrl = BuildNatsProbeUrl(natsHost);
        var natsCacheKey = BuildNatsCacheKey(natsHost);
        if (serverConfig.NatsUseWssExternal && !string.IsNullOrWhiteSpace(natsProbeUrl) && !string.IsNullOrWhiteSpace(natsCacheKey))
            natsTlsCertHash = await _tlsCertProbe.GetExpectedTlsCertHashAsync(natsProbeUrl, natsCacheKey, HttpContext.RequestAborted);

        return Ok(new
        {
            resolved.RecoveryEnabled,
            resolved.DiscoveryEnabled,
            resolved.P2PFilesEnabled,
            resolved.SupportEnabled,
            MeshCentralEnabledEffective = meshCentralEnabledEffective,
            resolved.ChatAIEnabled,
            resolved.KnowledgeBaseEnabled,
            AppStoreEnabled = resolved.AppStorePolicy != AppStorePolicyType.Disabled,
            resolved.InventoryIntervalHours,
            resolved.AutoUpdate,
            resolved.AgentUpdate,
            resolved.AgentHeartbeatIntervalSeconds,
            resolved.AgentOnlineGraceSeconds,
            resolved.SiteId,
            resolved.ClientId,
            resolved.ResolvedAt,
            NatsServerHost = natsHost,
            NatsUseWssExternal = serverConfig.NatsUseWssExternal,
            EnforceTlsHashValidation = enforceTls,
            HandshakeEnabled = handshakeEnabled,
            ApiTlsCertHash = apiTlsCertHash,
            NatsTlsCertHash = natsTlsCertHash
        });
    }

    /// <summary>
    /// Agent reports a TLS hash mismatch, triggering cache invalidation and re-probe.
    /// </summary>
    [HttpPost("me/tls-mismatch")]
    public async Task<IActionResult> ReportTlsMismatch([FromBody] AgentTlsMismatchReport? report, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out _))
            return Unauthorized(new { error = "Agent not authenticated." });

        if (report is null || string.IsNullOrWhiteSpace(report.Target))
            return BadRequest(new { error = "Target is required." });

        var target = report.Target.Trim().ToLowerInvariant();
        if (target != "api" && target != "nats")
            return BadRequest(new { error = "Target must be 'api' or 'nats'." });

        string? newHash = null;
        if (target == "api")
        {
            _tlsCertProbe.InvalidateCache(AgentTlsCertificateProbe.ApiCacheKey);
            newHash = await _tlsCertProbe.GetExpectedTlsCertHashAsync(ct);
        }
        else
        {
            var serverConfig = await _configService.GetServerConfigAsync();
            var natsHost = string.IsNullOrWhiteSpace(serverConfig.NatsServerHostExternal)
                ? serverConfig.NatsServerHostInternal
                : serverConfig.NatsServerHostExternal;
            var natsProbeUrl = BuildNatsProbeUrl(natsHost);
            var natsCacheKey = BuildNatsCacheKey(natsHost);
            if (!string.IsNullOrWhiteSpace(natsCacheKey))
                _tlsCertProbe.InvalidateCache(natsCacheKey);
            if (!string.IsNullOrWhiteSpace(natsProbeUrl) && !string.IsNullOrWhiteSpace(natsCacheKey))
                newHash = await _tlsCertProbe.GetExpectedTlsCertHashAsync(natsProbeUrl, natsCacheKey, ct);
        }

        return Ok(new { expectedHash = newHash });
    }

    /// <summary>
    /// Returns sync manifest listing all resources the agent should periodically sync.
    /// </summary>
    [HttpGet("me/sync-manifest")]
    public async Task<IActionResult> GetSyncManifest(CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var site = await _siteRepo.GetByIdAsync(agent!.SiteId);
        if (site is null) return NotFound(new { error = "Site not found for this agent." });

        var serverConfig = await _configService.GetServerConfigAsync();
        var clientConfig = await _configService.GetClientConfigAsync(site.ClientId);
        var siteConfig = await _configService.GetSiteConfigAsync(site.Id);
        var automationFingerprint = await _automationTaskService.GetPolicyFingerprintForAgentAsync(agentId, cancellationToken);

        var wingetApps = await _appStoreService.GetEffectiveAppsAsync(site.ClientId, site.Id, agent.Id, AppInstallationType.Winget, cancellationToken);
        var chocolateyApps = await _appStoreService.GetEffectiveAppsAsync(site.ClientId, site.Id, agent.Id, AppInstallationType.Chocolatey, cancellationToken);
        var customApps = await _appStoreService.GetEffectiveAppsAsync(site.ClientId, site.Id, agent.Id, AppInstallationType.Custom, cancellationToken);
        var agentUpdateManifest = await _agentUpdateService.GetManifestAsync(agentId, new AgentUpdateManifestRequest(agent.AgentVersion, null, null, null), cancellationToken);

        var resources = new List<AgentSyncManifestResourceDto>
        {
            new() { Resource = SyncResourceType.Configuration, Revision = $"cfg:{serverConfig.Version}:{clientConfig?.Version ?? 0}:{siteConfig?.Version ?? 0}", RecommendedSyncInSeconds = 300, Endpoint = "/api/v1/agent-auth/me/configuration" },
            new() { Resource = SyncResourceType.AutomationPolicy, Revision = $"automation:{automationFingerprint}", RecommendedSyncInSeconds = 180, Endpoint = "/api/v1/agent-auth/me/automation/policy-sync" },
            new() { Resource = SyncResourceType.AppStore, Variant = AppInstallationType.Winget.ToString(), Revision = $"app-store:winget:{ComputeAppStoreRevision(wingetApps)}", RecommendedSyncInSeconds = 900, Endpoint = "/api/v1/agent-auth/me/app-store?installationType=Winget" },
            new() { Resource = SyncResourceType.AppStore, Variant = AppInstallationType.Chocolatey.ToString(), Revision = $"app-store:chocolatey:{ComputeAppStoreRevision(chocolateyApps)}", RecommendedSyncInSeconds = 900, Endpoint = "/api/v1/agent-auth/me/app-store?installationType=Chocolatey" },
            new() { Resource = SyncResourceType.AppStore, Variant = AppInstallationType.Custom.ToString(), Revision = $"app-store:custom:{ComputeAppStoreRevision(customApps)}", RecommendedSyncInSeconds = 900, Endpoint = "/api/v1/agent-auth/me/app-store?installationType=Custom" },
            new() { Resource = SyncResourceType.AgentUpdate, Revision = agentUpdateManifest.Revision, RecommendedSyncInSeconds = 600, Endpoint = "/api/v1/agent-auth/me/update/manifest" }
        };

        var manifest = new AgentSyncManifestDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            RecommendedPollSeconds = 900,
            MaxStaleSeconds = 86400,
            Resources = resources
        };

        return Ok(manifest);
    }

    /// <summary>
    /// Issues NATS credentials for the authenticated agent.
    /// </summary>
    [HttpPost("me/nats-credentials")]
    public async Task<IActionResult> IssueNatsCredentials(CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: true);
        if (blocked is not null)
            return blocked;

        try
        {
            var credentials = await _natsCredentialsService.IssueForAgentAsync(agentId, ct);
            return Ok(credentials);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }
}
