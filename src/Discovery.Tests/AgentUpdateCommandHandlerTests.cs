using Discovery.Core.Cqrs.AgentUpdates.Commands;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentUpdates.CommandHandlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

public class AgentUpdateCommandHandlerTests
{
    [Test]
    public async Task RebuildAgentCommandHandler_ShouldBuildAndPublishStage2Installer()
    {
        var packageService = new FakeAgentPackageService();
        var updateService = new FakeAgentUpdateService();
        var syncPublisher = new FakeSyncInvalidationPublisher();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentPackage:InstallerContentType"] = "application/x-msdownload"
            })
            .Build();

        var handler = new RebuildAgentCommandHandler(
            packageService,
            updateService,
            syncPublisher,
            configuration,
            NullLogger<RebuildAgentCommandHandler>.Instance);

        var result = await handler.Handle(
            new RebuildAgentCommand(
                Version: "2.5.7",
                Platform: null,
                Architecture: null,
                ArtifactType: null,
                SignatureThumbprint: "thumb-123",
                Actor: "tester"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.Not.Null);

        Assert.Multiple(() =>
        {
            Assert.That(packageService.PrebuildCalls, Is.EqualTo(1));
            Assert.That(packageService.BuildUpdateInstallerCalls, Is.EqualTo(1));
            Assert.That(updateService.LastRefreshRequest, Is.Not.Null);
            Assert.That(updateService.LastRefreshRequest!.Version, Is.EqualTo("2.5.7"));
            Assert.That(updateService.LastRefreshRequest.Platform, Is.EqualTo("windows"));
            Assert.That(updateService.LastRefreshRequest.Architecture, Is.EqualTo("amd64"));
            Assert.That(updateService.LastRefreshRequest.ArtifactType, Is.EqualTo(AgentReleaseArtifactType.Installer));
            Assert.That(updateService.LastRefreshRequest.FileName, Is.EqualTo("discovery-agent-install.exe"));
            Assert.That(updateService.LastRefreshRequest.ContentType, Is.EqualTo("application/x-msdownload"));
            Assert.That(updateService.LastRefreshRequest.SignatureThumbprint, Is.EqualTo("thumb-123"));
            Assert.That(updateService.LastRefreshRequest.Actor, Is.EqualTo("tester"));
            Assert.That(syncPublisher.PublishGlobalCalls, Is.EqualTo(1));
            Assert.That(syncPublisher.LastResource, Is.EqualTo(SyncResourceType.AgentUpdate));
            Assert.That(syncPublisher.LastReason, Is.EqualTo("agent-build-refreshed-manual"));
            Assert.That(result.Value!.Version, Is.EqualTo("2.5.7"));
            Assert.That(result.Value.FileName, Is.EqualTo("discovery-agent-install.exe"));
        });
    }

    [Test]
    public async Task RebuildAgentCommandHandler_ShouldFallbackToCurrentBuildVersion_WhenCommandVersionIsMissing()
    {
        var packageService = new FakeAgentPackageService();
        var updateService = new FakeAgentUpdateService
        {
            CurrentBuild = new AgentUpdateBuild
            {
                Id = Guid.NewGuid(),
                Version = "3.1.4",
                Platform = "windows",
                Architecture = "amd64",
                ArtifactType = AgentReleaseArtifactType.Installer,
                FileName = "current.exe",
                ContentType = "application/x-msdownload",
                Sha256 = "hash"
            }
        };
        var configuration = new ConfigurationBuilder().Build();

        var handler = new RebuildAgentCommandHandler(
            packageService,
            updateService,
            new FakeSyncInvalidationPublisher(),
            configuration,
            NullLogger<RebuildAgentCommandHandler>.Instance);

        var result = await handler.Handle(new RebuildAgentCommand(), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(updateService.LastRefreshRequest, Is.Not.Null);
        Assert.That(updateService.LastRefreshRequest!.Version, Is.EqualTo("3.1.4"));
    }

    private sealed class FakeAgentPackageService : IAgentPackageService
    {
        public int PrebuildCalls { get; private set; }
        public int BuildUpdateInstallerCalls { get; private set; }

        public Task PrebuildBaseBinaryAsync(bool forceRebuild = false, CancellationToken cancellationToken = default)
        {
            PrebuildCalls++;
            return Task.CompletedTask;
        }

        public Task<(byte[] Content, string FileName)> BuildUpdateInstallerAsync(CancellationToken cancellationToken = default)
        {
            BuildUpdateInstallerCalls++;
            return Task.FromResult<(byte[] Content, string FileName)>(([1, 2, 3, 4], "discovery-agent-install.exe"));
        }

        public Task<byte[]> BuildPortablePackageAsync(string rawDeployToken, string? publicApiBaseUrl = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(byte[] Content, string FileName)> BuildInstallerAsync(string rawDeployToken, string? publicApiBaseUrl = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(byte[] Content, string FileName)> BuildBootstrapInstallerAsync(string rawDeployToken, string? publicApiBaseUrl = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(byte[] Content, string FileName)> BuildGenericInstallerAsync(bool forceRebuild = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentRepositorySyncResult> SyncRepositoryAsync(string branch, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeAgentUpdateService : IAgentUpdateService
    {
        public AgentUpdateBuild? CurrentBuild { get; set; }
        public RefreshRequest? LastRefreshRequest { get; private set; }

        public Task<AgentUpdateBuild?> GetCurrentBuildAsync(string? platform = null, string? architecture = null, AgentReleaseArtifactType? artifactType = null, CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentBuild);

        public async Task<AgentUpdateBuild> RefreshCurrentBuildAsync(string version, string platform, string architecture, AgentReleaseArtifactType artifactType, string fileName, string contentType, Stream content, string? signatureThumbprint = null, string? actor = null, CancellationToken cancellationToken = default)
        {
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);

            LastRefreshRequest = new RefreshRequest(
                version,
                platform,
                architecture,
                artifactType,
                fileName,
                contentType,
                signatureThumbprint,
                actor,
                buffer.ToArray());

            return new AgentUpdateBuild
            {
                Id = Guid.NewGuid(),
                Version = version,
                Platform = platform,
                Architecture = architecture,
                ArtifactType = artifactType,
                FileName = fileName,
                ContentType = contentType,
                StorageObjectKey = "agent-updates/current/discovery-agent-install.exe",
                StorageBucket = "local-stage2",
                StorageProviderType = 0,
                Sha256 = "abc123",
                SizeBytes = buffer.Length,
                SignatureThumbprint = signatureThumbprint,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                CreatedBy = actor,
                UpdatedBy = actor,
                IsActive = true
            };
        }

        public Task<IReadOnlyList<AgentRelease>> ListReleasesAsync(bool includeInactive = false, string? channel = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentRelease?> GetReleaseAsync(Guid releaseId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentRelease> CreateReleaseAsync(AgentReleaseWriteRequest request, string? actor = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentRelease> UpdateReleaseAsync(Guid releaseId, AgentReleaseWriteRequest request, string? actor = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentRelease> PromoteReleaseAsync(Guid releaseId, PromoteAgentReleaseRequest request, string? actor = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentCommand> TriggerForceUpdateAsync(Guid agentId, ForceAgentUpdateRequest request, string? actor = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteReleaseAsync(Guid releaseId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentReleaseArtifact> UploadArtifactAsync(Guid releaseId, string platform, string architecture, AgentReleaseArtifactType artifactType, string fileName, string contentType, Stream content, string? signatureThumbprint = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AgentUpdateEvent>> GetEventsByAgentAsync(Guid agentId, int limit = 100, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentUpdateRolloutDashboardDto> GetRolloutDashboardAsync(Guid? clientId = null, Guid? siteId = null, int limit = 200, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentUpdateManifestDto> GetManifestAsync(Guid agentId, AgentUpdateManifestRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentUpdateRedirectPayload?> GetPresignedDownloadUrlAsync(Guid agentId, AgentUpdateDownloadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentUpdateRedirectPayload?> GetDirectDownloadUrlAsync(Guid agentId, AgentUpdateDownloadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentUpdateEvent> RecordEventAsync(Guid agentId, AgentUpdateReportRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> GetCurrentBuildLocalPathAsync(string? platform = null, string? architecture = null, AgentReleaseArtifactType? artifactType = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeSyncInvalidationPublisher : ISyncInvalidationPublisher
    {
        public int PublishGlobalCalls { get; private set; }
        public SyncResourceType? LastResource { get; private set; }
        public string? LastReason { get; private set; }

        public Task PublishGlobalAsync(SyncResourceType resource, string reason, AppInstallationType? installationType = null, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            PublishGlobalCalls++;
            LastResource = resource;
            LastReason = reason;
            return Task.CompletedTask;
        }

        public Task PublishByScopeAsync(SyncResourceType resource, AppApprovalScopeType scopeType, Guid? scopeId, string reason, AppInstallationType? installationType = null, string? correlationId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed record RefreshRequest(
        string Version,
        string Platform,
        string Architecture,
        AgentReleaseArtifactType ArtifactType,
        string FileName,
        string ContentType,
        string? SignatureThumbprint,
        string? Actor,
        byte[] Content);
}