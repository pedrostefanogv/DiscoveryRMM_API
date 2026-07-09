using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Misc;
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
    ISiteRepository siteRepo
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
        if (site is not null)
        {
            clientConfig = await configService.GetClientConfigAsync(site.ClientId);
            siteConfig = await configService.GetSiteConfigAsync(agent.SiteId);
        }

        var policy = siteConfig?.AppStorePolicy ?? clientConfig?.AppStorePolicy ?? serverConfig.AppStorePolicy;
        var enabled = serverConfig.AppStorePolicy != Core.Enums.AppStorePolicyType.Disabled;

        return Result<object>.Success(new
        {
            enabled,
            policy = policy.ToString(),
            installationType = q.InstallationType
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
            return Result<object>.Failure(Error.Validation("Invalid custom field payload."));

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
            expiresAtUtc = token.ExpiresAtUtc
        });
    }
}

public sealed class GetAgentUpdateManifestHandler(
    IAgentUpdateService updateService
) : IRequestHandler<GetAgentUpdateManifestQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentUpdateManifestQuery q, CancellationToken ct)
    {
        var manifest = await updateService.GetManifestAsync(q.AgentId, new Core.DTOs.AgentUpdateManifestRequest
        {
            CurrentVersion = q.CurrentVersion,
            Platform = q.Platform,
            Architecture = q.Architecture,
            ArtifactType = TryParseArtifactType(q.ArtifactType)
        }, ct);

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
        var download = await updateService.GetPresignedDownloadUrlAsync(q.AgentId, new Core.DTOs.AgentUpdateDownloadRequest
        {
            ReleaseId = q.ReleaseId,
            Version = q.Version,
            Platform = q.Platform,
            Architecture = q.Architecture,
            ArtifactType = Enum.TryParse<Core.Enums.AgentReleaseArtifactType>(q.ArtifactType, ignoreCase: true, out var at) ? at : null
        }, ct);

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
        // The payload is an object, try to deserialize it as an update report
        var eventRecord = await updateService.RecordEventAsync(cmd.AgentId, new Core.DTOs.AgentUpdateReportRequest(), ct);
        return Result<object>.Success(eventRecord);
    }
}