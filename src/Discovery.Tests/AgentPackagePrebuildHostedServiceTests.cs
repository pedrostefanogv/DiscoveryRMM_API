using System.Diagnostics;
using Discovery.Api.Services;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

public class AgentPackagePrebuildHostedServiceTests
{
    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(canceled: true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class FakeRebuildExecutor : IAgentRebuildExecutor
    {
        private readonly TaskCompletionSource<AgentBuildDto> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public string? LastActor;

        public Task<AgentBuildDto> ExecuteAsync(string actor, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastActor = actor;
            return _tcs.Task.WaitAsync(cancellationToken);
        }

        public void Complete(AgentBuildDto build) => _tcs.TrySetResult(build);
    }

    private sealed class StubAgentPackageService(string version, string commitHash) : IAgentPackageService
    {
        public int PrebuildCalls;
        public int GenericWarmupCalls;

        public Task PrebuildBaseBinaryAsync(bool forceRebuild = false, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PrebuildCalls);
            return Task.CompletedTask;
        }

        public Task<string> GetAgentCommitHashAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(commitHash);

        public Task<string> GetAgentSourceVersionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(version);

        public Task<GenericInstallerArtifact> GetGenericInstallerArtifactAsync(bool forceRebuild = false, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref GenericWarmupCalls);
            return Task.FromResult(new GenericInstallerArtifact(
                LocalPath: Path.Combine(Path.GetTempPath(), "generic-noop.exe"),
                FileName: "discovery-agent-generic.exe",
                SizeBytes: 123,
                Sha256: "abc",
                Version: version,
                CommitHash: commitHash,
                CacheKey: "key",
                S3ObjectKey: null));
        }

        public Task<(byte[] Content, string FileName)> BuildInstallerAsync(string rawDeployToken, string? publicApiBaseUrl = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<(byte[] Content, string FileName)> BuildBootstrapInstallerAsync(string rawDeployToken, string? publicApiBaseUrl = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<(byte[] Content, string FileName)> BuildGenericInstallerAsync(bool forceRebuild = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<GenericInstallerMetadata?> GetCachedGenericInstallerMetadataAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<(byte[] Content, string FileName)> BuildUpdateInstallerAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentRepositorySyncResult> SyncRepositoryAsync(string branch, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class StubAgentUpdateService(AgentUpdateBuild? currentBuild, string? localPath) : IAgentUpdateService
    {
        public Task<AgentUpdateBuild?> GetCurrentBuildAsync(string? platform = null, string? architecture = null, AgentReleaseArtifactType? artifactType = null, CancellationToken cancellationToken = default)
            => Task.FromResult(currentBuild);

        public Task<string?> GetCurrentBuildLocalPathAsync(string? platform = null, string? architecture = null, AgentReleaseArtifactType? artifactType = null, CancellationToken cancellationToken = default)
            => Task.FromResult(localPath);

        public Task<AgentUpdateBuild> RefreshCurrentBuildAsync(string version, string platform, string architecture, AgentReleaseArtifactType artifactType, string fileName, string contentType, Stream content, string? signatureThumbprint = null, string? commitHash = null, string? actor = null, bool cleanupOldBuilds = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentRelease>> ListReleasesAsync(bool includeInactive = false, string? channel = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentRelease?> GetReleaseAsync(Guid releaseId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentRelease> CreateReleaseAsync(AgentReleaseWriteRequest request, string? actor = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentRelease> UpdateReleaseAsync(Guid releaseId, AgentReleaseWriteRequest request, string? actor = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentRelease> PromoteReleaseAsync(Guid releaseId, PromoteAgentReleaseRequest request, string? actor = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentCommand> TriggerForceUpdateAsync(Guid agentId, ForceAgentUpdateRequest request, string? actor = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteReleaseAsync(Guid releaseId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentReleaseArtifact> UploadArtifactAsync(Guid releaseId, string platform, string architecture, AgentReleaseArtifactType artifactType, string fileName, string contentType, Stream content, string? signatureThumbprint = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentUpdateEvent>> GetEventsByAgentAsync(Guid agentId, int limit = 100, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentUpdateRolloutDashboardDto> GetRolloutDashboardAsync(Guid? clientId = null, Guid? siteId = null, int limit = 200, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentUpdateManifestDto> GetManifestAsync(Guid agentId, AgentUpdateManifestRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentUpdateRedirectPayload?> GetPresignedDownloadUrlAsync(Guid agentId, AgentUpdateDownloadRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentUpdateRedirectPayload?> GetDirectDownloadUrlAsync(Guid agentId, AgentUpdateDownloadRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentUpdateEvent> RecordEventAsync(Guid agentId, AgentUpdateReportRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> CleanupInactiveBuildsAsync(int keepRecentCount = 3, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail("Condição não satisfeita no tempo esperado.");
            await Task.Delay(25);
        }
    }

    [Test]
    public async Task StartupPrebuild_WhenStage2MatchesVersionCommitAndDiskFile_SkipsRebuildCoordinator()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stage2-test-{Guid.NewGuid():N}.exe");
        await File.WriteAllBytesAsync(tempFile, [1, 2, 3]);
        try
        {
            var packageService = new StubAgentPackageService("1.4.0", "abcd1234");
            var currentBuild = new AgentUpdateBuild
            {
                Id = Guid.NewGuid(),
                Version = "1.4.0",
                CommitHash = "abcd1234",
                Platform = "windows",
                Architecture = "amd64",
                FileName = "discovery-agent-install.exe",
                IsActive = true
            };
            var updateService = new StubAgentUpdateService(currentBuild, tempFile);

            var services = new ServiceCollection();
            services.AddSingleton<IAgentPackageService>(packageService);
            services.AddSingleton<IAgentUpdateService>(updateService);
            await using var provider = services.BuildServiceProvider();

            var executor = new FakeRebuildExecutor();
            var coordinator = new AgentRebuildCoordinator(executor, new FakeApplicationLifetime(), NullLogger<AgentRebuildCoordinator>.Instance);
            var config = new ConfigurationBuilder().Build();

            var hosted = new AgentPackagePrebuildHostedService(
                provider,
                config,
                new FakeApplicationLifetime(),
                coordinator,
                NullLogger<AgentPackagePrebuildHostedService>.Instance);

            await hosted.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => packageService.GenericWarmupCalls == 1);
            await hosted.StopAsync(CancellationToken.None);

            Assert.That(executor.Calls, Is.EqualTo(0), "Não deve disparar rebuild quando stage2 já está atualizado em disco.");
            Assert.That(packageService.PrebuildCalls, Is.EqualTo(1));
            Assert.That(coordinator.GetStatus().State, Is.EqualTo(AgentRebuildState.Idle));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Test]
    public async Task StartupPrebuild_WhenCommitDiffers_QueuesInCoordinatorAndConcurrentTriggerJoins()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stage2-test-{Guid.NewGuid():N}.exe");
        await File.WriteAllBytesAsync(tempFile, [1, 2, 3]);
        try
        {
            var packageService = new StubAgentPackageService("1.4.0", "newcommit");
            var currentBuild = new AgentUpdateBuild
            {
                Id = Guid.NewGuid(),
                Version = "1.4.0",
                CommitHash = "oldcommit",
                Platform = "windows",
                Architecture = "amd64",
                FileName = "discovery-agent-install.exe",
                IsActive = true
            };
            var updateService = new StubAgentUpdateService(currentBuild, tempFile);

            var services = new ServiceCollection();
            services.AddSingleton<IAgentPackageService>(packageService);
            services.AddSingleton<IAgentUpdateService>(updateService);
            await using var provider = services.BuildServiceProvider();

            var executor = new FakeRebuildExecutor();
            var coordinator = new AgentRebuildCoordinator(executor, new FakeApplicationLifetime(), NullLogger<AgentRebuildCoordinator>.Instance);
            var config = new ConfigurationBuilder().Build();

            var hosted = new AgentPackagePrebuildHostedService(
                provider,
                config,
                new FakeApplicationLifetime(),
                coordinator,
                NullLogger<AgentPackagePrebuildHostedService>.Instance);

            await hosted.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => executor.Calls == 1);

            // Disparo concorrente (ex.: POST /build/rebuild do script de update) deve aderir ao mesmo RunId.
            var (startedByScript, joinedStatus) = coordinator.TryStart("localhost-script");
            Assert.That(startedByScript, Is.False);
            Assert.That(joinedStatus.Actor, Is.EqualTo("startup-prebuild"));

            executor.Complete(new AgentBuildDto(
                Guid.NewGuid(), "1.4.0", "windows", "amd64",
                "discovery-agent-install.exe", "sha", DateTime.UtcNow, null));

            var final = await coordinator.WaitForCompletionAsync(joinedStatus.RunId!.Value, CancellationToken.None);
            Assert.That(final.State, Is.EqualTo(AgentRebuildState.Succeeded));
            Assert.That(executor.Calls, Is.EqualTo(1));

            await hosted.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
