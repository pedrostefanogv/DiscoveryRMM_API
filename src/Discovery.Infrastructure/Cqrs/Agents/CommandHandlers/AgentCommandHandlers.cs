using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Commands;
using Discovery.Core.Cqrs.Agents.Queries;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class RefreshAgentBuildCommandHandler(
    IAgentUpdateService agentUpdateService
) : IRequestHandler<RefreshAgentBuildCommand, Result<AgentBuildResult>>
{
    public async Task<Result<AgentBuildResult>> Handle(RefreshAgentBuildCommand cmd, CancellationToken ct)
    {
        var artifactType = Enum.TryParse<AgentReleaseArtifactType>(cmd.ArtifactType, true, out var at)
            ? at : AgentReleaseArtifactType.Installer;

        var build = await agentUpdateService.RefreshCurrentBuildAsync(
            cmd.Version, cmd.Platform, cmd.Architecture, artifactType,
            cmd.FileName, cmd.ContentType, cmd.Content,
            cmd.SignatureThumbprint, cmd.Actor, ct);

        return Result<AgentBuildResult>.Success(new AgentBuildResult(
            build.Id, build.Version, build.Sha256, build.CreatedAt));
    }
}

public sealed class PromoteAgentBuildCommandHandler(
    IAgentUpdateService agentUpdateService
) : IRequestHandler<PromoteAgentBuildCommand, Result<AgentBuildResult>>
{
    public async Task<Result<AgentBuildResult>> Handle(PromoteAgentBuildCommand cmd, CancellationToken ct)
    {
        var request = new PromoteAgentReleaseRequest(TargetChannel: cmd.Channel, IsActive: false);
        var release = await agentUpdateService.PromoteReleaseAsync(cmd.BuildId, request, null, ct);
        return Result<AgentBuildResult>.Success(new AgentBuildResult(release.Id, release.Version, string.Empty, release.CreatedAt));
    }
}