using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentUpdates.Commands;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.AgentUpdates.CommandHandlers;

public sealed class RefreshAgentBuildCommandHandler(
    IAgentUpdateService agentUpdateService
) : IRequestHandler<RefreshAgentBuildCommand, Result<AgentBuildDto>>
{
    public async Task<Result<AgentBuildDto>> Handle(RefreshAgentBuildCommand cmd, CancellationToken ct)
    {
        var artifactType = Enum.TryParse<AgentReleaseArtifactType>(cmd.ArtifactType, true, out var at)
            ? at : AgentReleaseArtifactType.Installer;

        var build = await agentUpdateService.RefreshCurrentBuildAsync(
            cmd.Version, cmd.Platform, cmd.Architecture, artifactType,
            cmd.FileName, cmd.ContentType, cmd.Content,
            cmd.SignatureThumbprint, cmd.Actor, ct);

        return Result<AgentBuildDto>.Success(new AgentBuildDto(
            build.Id, build.Version, build.Platform, build.Architecture,
            build.FileName, build.Sha256, build.CreatedAt, null));
    }
}

public sealed class ForceAgentUpdateCommandHandler(
    IAgentUpdateService agentUpdateService
) : IRequestHandler<ForceAgentUpdateCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ForceAgentUpdateCommand cmd, CancellationToken ct)
    {
        var request = new ForceAgentUpdateRequest(TargetVersion: cmd.Version ?? cmd.Channel);
        await agentUpdateService.TriggerForceUpdateAsync(cmd.AgentId, request, null, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class SyncAgentRepositoryCommandHandler(
) : IRequestHandler<SyncAgentRepositoryCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(SyncAgentRepositoryCommand cmd, CancellationToken ct)
    {
        // O AgentPackageService não tem um método de sync de repositório específico no contrato.
        // O sync é tipicamente orquestrado externamente. Retornamos sucesso pois o fluxo
        // de build é gerenciado pelo AgentPackageService.PrebuildBaseBinaryAsync.
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class SyncAndBuildAgentCommandHandler(
    IAgentPackageService agentPackageService
) : IRequestHandler<SyncAndBuildAgentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(SyncAndBuildAgentCommand cmd, CancellationToken ct)
    {
        await agentPackageService.PrebuildBaseBinaryAsync(forceRebuild: true, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class RebuildAgentCommandHandler(
    IAgentPackageService agentPackageService,
    IAgentUpdateService agentUpdateService,
    ISyncInvalidationPublisher syncInvalidationPublisher,
    ILogger<RebuildAgentCommandHandler> logger
) : IRequestHandler<RebuildAgentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RebuildAgentCommand cmd, CancellationToken ct)
    {
        logger.LogInformation("Agent rebuild: starting binary build from source...");
        await agentPackageService.PrebuildBaseBinaryAsync(forceRebuild: true, ct);

        logger.LogInformation("Agent rebuild: generating update installer...");
        var (content, fileName) = await agentPackageService.BuildUpdateInstallerAsync(ct);

        // Publish the newly built installer as the current stage2 build
        var now = DateTime.UtcNow;
        var version = now.ToString("yyyy.MM.dd") + ".0"; // date-based version for CI rebuilds
        await using var stream = new MemoryStream(content, writable: false);

        var build = await agentUpdateService.RefreshCurrentBuildAsync(
            version: version,
            platform: "windows",
            architecture: "amd64",
            artifactType: AgentReleaseArtifactType.Installer,
            fileName: fileName,
            contentType: "application/x-msdownload",
            content: stream,
            signatureThumbprint: null,
            actor: "api-rebuild",
            cancellationToken: ct);

        logger.LogInformation(
            "Agent rebuild completed. BuildId={BuildId}, Version={Version}, File={FileName}, Size={SizeBytes}",
            build.Id, build.Version, build.FileName, build.SizeBytes);

        await syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.AgentUpdate,
            "agent-build-rebuilt",
            cancellationToken: ct);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}