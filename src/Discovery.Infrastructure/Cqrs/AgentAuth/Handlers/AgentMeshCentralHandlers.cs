using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.MeshCentral;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class CreateMeshCentralEmbedUrlHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IMeshCentralEmbeddingService embeddingService
) : IRequestHandler<CreateMeshCentralEmbedUrlCommand, Result<object>>
{
    public async Task<Result<object>> Handle(CreateMeshCentralEmbedUrlCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Result<object>.Failure(Error.NotFound("Site not found."));

        try
        {
            var result = await embeddingService.GenerateAgentEmbedUrlAsync(
                agent,
                site.ClientId,
                cmd.ViewMode ?? 0,
                cmd.HideMask != null ? int.Parse(cmd.HideMask) : null,
                cmd.MeshNodeId,
                cmd.GotoDeviceName,
                ct);

            return Result<object>.Success(new
            {
                result.Url,
                result.ExpiresAtUtc,
                result.ViewMode,
                result.HideMask
            });
        }
        catch (Exception ex)
        {
            return Result<object>.Failure(Error.Internal($"MeshCentral embed failed: {ex.Message}"));
        }
    }
}

public sealed class GetMeshCentralInstallHandler(
    IAgentRepository agentRepo,
    IConfigurationService configService
) : IRequestHandler<GetMeshCentralInstallQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetMeshCentralInstallQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var serverConfig = await configService.GetServerConfigAsync();

        return Result<object>.Success(new
        {
            downloadUrl = (string?)null,  // TODO: MeshCentral agent binary URL
            installCommand = (string?)null,
            groupPolicyProfile = serverConfig.MeshCentralGroupPolicyProfile,
            enabled = true
        });
    }
}