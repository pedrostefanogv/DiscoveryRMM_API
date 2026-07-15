using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Misc;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentIdentityHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo
) : IRequestHandler<GetAgentIdentityQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentIdentityQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);

        return Result<object>.Success(new
        {
            agent.Id,
            agent.Hostname,
            agent.DisplayName,
            agent.OperatingSystem,
            agent.OsVersion,
            agent.AgentVersion,
            agent.Status,
            ClientId = site?.ClientId,
            agent.SiteId,
            agent.ZeroTouchPending,
            agent.MaintenanceEnabled,
            agent.LastSeenAt
        });
    }
}

public sealed class GetAppStoreEffectiveHandler(
    IAgentRepository agentRepo,
    IConfigurationService configService,
    ISiteRepository siteRepo,
    IAppStoreService appStoreService
) : IRequestHandler<GetAppStoreEffectiveQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAppStoreEffectiveQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var serverConfig = await configService.GetServerConfigAsync();
        ClientConfiguration? clientConfig = null;
        SiteConfiguration? siteConfig = null;
        Guid? clientId = null;
        if (site is not null)
        {
            clientId = site.ClientId;
            clientConfig = await configService.GetClientConfigAsync(site.ClientId);
            siteConfig = await configService.GetSiteConfigAsync(agent.SiteId);
        }

        var policy = siteConfig?.AppStorePolicy ?? clientConfig?.AppStorePolicy ?? serverConfig.AppStorePolicy;
        var enabled = serverConfig.AppStorePolicy != Core.Enums.AppStorePolicyType.Disabled;

        if (!enabled)
            return Result<object>.Success(new { enabled = false, policy = policy.ToString(), installationType = q.InstallationType, count = 0, items = Array.Empty<object>() });

        // Busca os apps efetivos aprovados para o escopo do agent
        var apps = await appStoreService.GetEffectiveAppsAsync(clientId, agent.SiteId, agent.Id, 
            Enum.TryParse<Core.Enums.AppInstallationType>(q.InstallationType, true, out var instType) ? instType : Core.Enums.AppInstallationType.Winget, 
            ct);
        var items = apps.Select(a => new
        {
            installationType = a.InstallationType,
            packageId = a.PackageId,
            name = a.Name,
            description = a.Description,
            iconUrl = a.IconUrl,
            publisher = a.Publisher,
            version = a.Version,
            installCommand = a.InstallCommand,
            installerUrlsByArch = a.InstallerUrlsByArch,
            autoUpdateEnabled = a.AutoUpdateEnabled,
            sourceScope = a.SourceScope
        }).ToList();

        return Result<object>.Success(new
        {
            enabled,
            policy = policy.ToString(),
            installationType = q.InstallationType,
            count = items.Count,
            items
        });
    }
}

public sealed class GetRuntimeCustomFieldsHandler(
    ICustomFieldService customFieldService
) : IRequestHandler<GetRuntimeCustomFieldsQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetRuntimeCustomFieldsQuery q, CancellationToken ct)
    {
        var fields = await customFieldService.GetRuntimeValuesForAgentAsync(q.AgentId, q.TaskId, q.ScriptId, ct);
        return Result<object>.Success(new { fields });
    }
}

public sealed class UpsertCollectedCustomFieldHandler(
    ICustomFieldService customFieldService
) : IRequestHandler<UpsertCollectedCustomFieldCommand, Result<object>>
{
    public async Task<Result<object>> Handle(UpsertCollectedCustomFieldCommand cmd, CancellationToken ct)
    {
        // cmd.Request is the raw JSON payload from the agent
        var inputJson = System.Text.Json.JsonSerializer.Serialize(cmd.Request);
        var input = System.Text.Json.JsonSerializer.Deserialize<Core.DTOs.AgentCustomFieldCollectedValueInput>(inputJson);

        if (input is null)
            return Result<object>.Failure(Error.Validation("customField", "Invalid custom field payload."));

        var result = await customFieldService.UpsertAgentCollectedValueAsync(cmd.AgentId, input, ct);
        return Result<object>.Success(new { saved = true, definitionId = result.DefinitionId });
    }
}

public sealed class IssueZeroTouchDeployTokenHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IDeployTokenService deployTokenService
) : IRequestHandler<IssueZeroTouchDeployTokenCommand, Result<object>>
{
    public async Task<Result<object>> Handle(IssueZeroTouchDeployTokenCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Result<object>.Failure(Error.NotFound("Site not found."));

        var (token, rawToken) = await deployTokenService.CreateZeroTouchTokenAsync(site.ClientId, agent.SiteId);

        return Result<object>.Success(new
        {
            tokenId = token.Id,
            rawToken,
            clientId = token.ClientId,
            siteId = token.SiteId,
            expiresAtUtc = token.ExpiresAt
        });
    }
}

public sealed class GetAgentUpdateManifestHandler(
    IAgentUpdateService updateService
) : IRequestHandler<GetAgentUpdateManifestQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentUpdateManifestQuery q, CancellationToken ct)
    {
        var manifest = await updateService.GetManifestAsync(q.AgentId, new Core.DTOs.AgentUpdateManifestRequest(
            q.CurrentVersion,
            q.Platform,
            q.Architecture,
            TryParseArtifactType(q.ArtifactType)
        ), ct);

        return Result<object>.Success(manifest);
    }

    private static Core.Enums.AgentReleaseArtifactType? TryParseArtifactType(string? artifactType)
    {
        if (string.IsNullOrWhiteSpace(artifactType)) return null;
        return Enum.TryParse<Core.Enums.AgentReleaseArtifactType>(artifactType, ignoreCase: true, out var result) ? result : null;
    }
}

public sealed class DownloadAgentUpdateHandler(
    IAgentUpdateService updateService
) : IRequestHandler<DownloadAgentUpdateQuery, Result<object>>
{
    public async Task<Result<object>> Handle(DownloadAgentUpdateQuery q, CancellationToken ct)
    {
        var download = await updateService.GetPresignedDownloadUrlAsync(q.AgentId, new Core.DTOs.AgentUpdateDownloadRequest(
            q.ReleaseId,
            q.Version,
            q.Platform,
            q.Architecture,
            Enum.TryParse<Core.Enums.AgentReleaseArtifactType>(q.ArtifactType, ignoreCase: true, out var at) ? at : null
        ), ct);

        if (download is null)
            return Result<object>.Failure(Error.NotFound("No update download available."));

        return Result<object>.Success(download);
    }
}

public sealed class ReportAgentUpdateHandler(
    IAgentUpdateService updateService
) : IRequestHandler<ReportAgentUpdateCommand, Result<object>>
{
    public async Task<Result<object>> Handle(ReportAgentUpdateCommand cmd, CancellationToken ct)
    {
        var eventRecord = await updateService.RecordEventAsync(cmd.AgentId, cmd.Request, ct);
        return Result<object>.Success(eventRecord);
    }
}