using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentUpdates.Commands;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Configuration;
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
    IConfiguration configuration,
    ILogger<RebuildAgentCommandHandler> logger
) : IRequestHandler<RebuildAgentCommand, Result<AgentBuildDto>>
{
    public async Task<Result<AgentBuildDto>> Handle(RebuildAgentCommand cmd, CancellationToken ct)
    {
        var platform = string.IsNullOrWhiteSpace(cmd.Platform) ? "windows" : cmd.Platform.Trim();
        var architecture = string.IsNullOrWhiteSpace(cmd.Architecture) ? "amd64" : cmd.Architecture.Trim();
        var artifactType = string.IsNullOrWhiteSpace(cmd.ArtifactType) ? AgentReleaseArtifactType.Installer :
            (Enum.TryParse<AgentReleaseArtifactType>(cmd.ArtifactType, true, out var parsedArtifactType)
                ? parsedArtifactType
                : AgentReleaseArtifactType.Installer);

        try
        {
            await agentPackageService.PrebuildBaseBinaryAsync(forceRebuild: true, ct);

            var (content, fileName) = artifactType == AgentReleaseArtifactType.Installer
                ? await agentPackageService.BuildUpdateInstallerAsync(ct)
                : await agentPackageService.BuildUpdateInstallerAsync(ct);

            var version = await ResolveBuildVersionAsync(cmd, agentUpdateService, ct);
            var contentType = ResolveInstallerContentType();

            await using var stream = new MemoryStream(content, writable: false);
            var build = await agentUpdateService.RefreshCurrentBuildAsync(
                version,
                platform,
                architecture,
                artifactType,
                fileName,
                contentType,
                stream,
                cmd.SignatureThumbprint,
                cmd.Actor,
                ct);

            await syncInvalidationPublisher.PublishGlobalAsync(
                SyncResourceType.AgentUpdate,
                "agent-build-refreshed-manual",
                cancellationToken: ct);

            return Result<AgentBuildDto>.Success(new AgentBuildDto(
                build.Id,
                build.Version,
                build.Platform,
                build.Architecture,
                build.FileName,
                build.Sha256,
                build.CreatedAt,
                build.SignatureThumbprint));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual agent rebuild/refresh failed.");
            return Result<AgentBuildDto>.Failure(Error.Internal($"Falha ao gerar e publicar o build do agente: {ex.Message}"));
        }
    }

    private async Task<string> ResolveBuildVersionAsync(
        RebuildAgentCommand cmd,
        IAgentUpdateService agentUpdateService,
        CancellationToken ct)
    {
        var configured = NormalizeSemanticVersion(cmd.Version);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var currentBuild = await agentUpdateService.GetCurrentBuildAsync(
            platform: string.IsNullOrWhiteSpace(cmd.Platform) ? "windows" : cmd.Platform,
            architecture: string.IsNullOrWhiteSpace(cmd.Architecture) ? "amd64" : cmd.Architecture,
            artifactType: AgentReleaseArtifactType.Installer,
            cancellationToken: ct);

        if (!string.IsNullOrWhiteSpace(currentBuild?.Version))
            return currentBuild.Version;

        var configuredStartupVersion = NormalizeSemanticVersion(configuration["AgentPackage:StartupStage2Version"]);
        if (!string.IsNullOrWhiteSpace(configuredStartupVersion))
            return configuredStartupVersion;

        var assemblyVersion = typeof(RebuildAgentCommandHandler).Assembly.GetName().Version;
        if (assemblyVersion is not null)
        {
            var fallback = $"{Math.Max(assemblyVersion.Major, 1)}.{Math.Max(assemblyVersion.Minor, 0)}.{Math.Max(assemblyVersion.Build, 0)}";
            if (SemanticVersion.TryParse(fallback, out _))
                return fallback;
        }

        return "1.0.0";
    }

    private string ResolveInstallerContentType()
        => configuration["AgentPackage:InstallerContentType"] is { Length: > 0 } configuredContentType
            ? configuredContentType
            : "application/x-msdownload";

    private static string? NormalizeSemanticVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
            return null;

        var normalized = rawVersion.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];

        return SemanticVersion.TryParse(normalized, out _) ? normalized : null;
    }
}