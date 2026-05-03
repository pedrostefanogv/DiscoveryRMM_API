using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

public class AgentUpdateServiceTests
{
    [Test]
    public async Task GetManifestAsync_ShouldReturnDirectUpdateWhenAgentIsEligible()
    {
        var agent = CreateAgent(version: "1.0.0");
        var release = CreateRelease("1.2.0");
        var policy = CreatePolicy(rolloutPercentage: 100);
        var service = CreateService(agent, policy, [release]);

        var manifest = await service.GetManifestAsync(
            agent.Id,
            new AgentUpdateManifestRequest(agent.AgentVersion, null, null, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest.Enabled, Is.True);
            Assert.That(manifest.UpdateAvailable, Is.True);
            Assert.That(manifest.RolloutEligible, Is.True);
            Assert.That(manifest.DirectUpdateSupported, Is.True);
            Assert.That(manifest.LatestVersion, Is.EqualTo("1.2.0"));
            Assert.That(manifest.FileName, Is.EqualTo("discovery-installer.exe"));
            Assert.That(manifest.Sha256, Is.EqualTo("abc123"));
            Assert.That(manifest.Message, Is.EqualTo("A newer agent version is available for download."));
        });
    }

    [Test]
    public async Task GetManifestAsync_ShouldBlockDirectUpdateWhenAgentIsOutsideRollout()
    {
        var agent = CreateAgent(version: "1.0.0");
        var release = CreateRelease("1.2.0");
        var policy = CreatePolicy(rolloutPercentage: 0);
        var service = CreateService(agent, policy, [release]);

        var manifest = await service.GetManifestAsync(
            agent.Id,
            new AgentUpdateManifestRequest(agent.AgentVersion, null, null, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(manifest.UpdateAvailable, Is.True);
            Assert.That(manifest.RolloutEligible, Is.False);
            Assert.That(manifest.DirectUpdateSupported, Is.False);
            Assert.That(manifest.Message, Is.EqualTo("A newer version exists, but this agent is outside the current rollout window."));
        });
    }

    [Test]
    public async Task RecordEventAsync_ShouldUpdateAgentVersionAfterInstallSucceeded()
    {
        var agent = CreateAgent(version: "1.0.0");
        var release = CreateRelease("1.2.0");
        var policy = CreatePolicy(rolloutPercentage: 100);
        var agentRepository = new FakeAgentRepository(agent);
        var eventRepository = new FakeAgentUpdateEventRepository();
        var service = CreateService(agent, policy, [release], agentRepository, eventRepository);

        var created = await service.RecordEventAsync(
            agent.Id,
            new AgentUpdateReportRequest(
                AgentUpdateEventType.InstallSucceeded,
                release.Id,
                "1.0.0",
                "1.2.0",
                "ok",
                "corr-1",
                DateTime.UtcNow,
                null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(created.EventType, Is.EqualTo(AgentUpdateEventType.InstallSucceeded));
            Assert.That(agent.AgentVersion, Is.EqualTo("1.2.0"));
            Assert.That(agentRepository.UpdateCalls, Is.EqualTo(1));
            Assert.That(eventRepository.Events, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task GetPresignedDownloadUrlAsync_ShouldReturnPayloadWhenManifestAllowsDirectUpdate()
    {
        var agent = CreateAgent(version: "1.0.0");
        var release = CreateRelease("1.2.0");
        var policy = CreatePolicy(rolloutPercentage: 100);
        var storage = new FakeObjectStorageService("https://storage.example.com/agent-update.exe");
        var service = CreateService(
            agent,
            policy,
            [release],
            storageFactory: new FakeObjectStorageProviderFactory(storage));

        var payload = await service.GetPresignedDownloadUrlAsync(
            agent.Id,
            new AgentUpdateDownloadRequest(release.Id, "1.2.0", null, null, null),
            CancellationToken.None);

        Assert.That(payload, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(payload!.DownloadUrl, Is.EqualTo("https://storage.example.com/agent-update.exe"));
            Assert.That(payload.Sha256, Is.EqualTo("abc123"));
            Assert.That(payload.FileName, Is.EqualTo("discovery-installer.exe"));
            Assert.That(storage.LastObjectKey, Is.EqualTo(release.Artifacts[0].StorageObjectKey));
            Assert.That(storage.LastTtlHours, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task GetPresignedDownloadUrlAsync_ShouldReturnNullWhenRequestedVersionDoesNotMatchManifest()
    {
        var agent = CreateAgent(version: "1.0.0");
        var release = CreateRelease("1.2.0");
        var policy = CreatePolicy(rolloutPercentage: 100);
        var storage = new FakeObjectStorageService("https://storage.example.com/agent-update.exe");
        var service = CreateService(
            agent,
            policy,
            [release],
            storageFactory: new FakeObjectStorageProviderFactory(storage));

        var payload = await service.GetPresignedDownloadUrlAsync(
            agent.Id,
            new AgentUpdateDownloadRequest(release.Id, "1.9.0", null, null, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(payload, Is.Null);
            Assert.That(storage.LastObjectKey, Is.Null);
        });
    }

    [Test]
    public async Task PromoteReleaseAsync_ShouldCloneReleaseIntoTargetChannelAndCopyArtifacts()
    {
        var agent = CreateAgent(version: "1.0.0");
        var sourceRelease = CreateRelease("1.2.0", channel: "beta");
        var policy = CreatePolicy(rolloutPercentage: 100);
        var releaseRepository = new FakeAgentReleaseRepository([sourceRelease]);
        var storage = new FakeObjectStorageService("https://storage.example.com/agent-update.exe");
        storage.SeedObject(sourceRelease.Artifacts[0].StorageObjectKey, sourceRelease.Artifacts[0].ContentType, [1, 2, 3, 4]);

        var service = CreateService(
            agent,
            policy,
            [sourceRelease],
            releaseRepository: releaseRepository,
            storageFactory: new FakeObjectStorageProviderFactory(storage));

        var promoted = await service.PromoteReleaseAsync(
            sourceRelease.Id,
            new PromoteAgentReleaseRequest("stable", true),
            "tester",
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(promoted.Id, Is.Not.EqualTo(sourceRelease.Id));
            Assert.That(promoted.Channel, Is.EqualTo("stable"));
            Assert.That(promoted.Version, Is.EqualTo(sourceRelease.Version));
            Assert.That(promoted.Artifacts, Has.Count.EqualTo(1));
            Assert.That(promoted.Artifacts[0].StorageObjectKey, Does.Contain("agent-updates/stable/1.2.0/windows/amd64/portable/"));
            Assert.That(storage.DownloadedKeys, Has.Count.EqualTo(1));
            Assert.That(storage.UploadedKeys, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void PromoteReleaseAsync_ShouldRejectPromotionToSameChannel()
    {
        var agent = CreateAgent(version: "1.0.0");
        var sourceRelease = CreateRelease("1.2.0", channel: "stable");
        var policy = CreatePolicy(rolloutPercentage: 100);
        var service = CreateService(agent, policy, [sourceRelease]);

        Assert.That(async () => await service.PromoteReleaseAsync(
                sourceRelease.Id,
                new PromoteAgentReleaseRequest("stable", true),
                "tester",
                CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.EqualTo("Source and target channels must be different for promotion."));
    }

    [Test]
    public async Task TriggerForceUpdateAsync_ShouldDispatchUpdateCommandWithNormalizedPayload()
    {
        var agent = CreateAgent(version: "1.0.0");
        var release = CreateRelease("1.2.0");
        var policy = CreatePolicy(rolloutPercentage: 100);
        var commandDispatcher = new FakeAgentCommandDispatcher();
        var service = CreateService(agent, policy, [release], commandDispatcher: commandDispatcher);

        var command = await service.TriggerForceUpdateAsync(
            agent.Id,
            new ForceAgentUpdateRequest("  manual-trigger  "),
            "tester",
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(command.CommandType, Is.EqualTo(CommandType.Update));
            Assert.That(command.Status, Is.EqualTo(CommandStatus.Sent));
            Assert.That(commandDispatcher.Commands, Has.Count.EqualTo(1));
        });

        using var payload = JsonDocument.Parse(commandDispatcher.Commands[0].Payload);
        var root = payload.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("action").GetString(), Is.EqualTo("force-update"));
            Assert.That(root.GetProperty("force").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("requestedBy").GetString(), Is.EqualTo("tester"));
            Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo("manual-trigger"));
        });
    }

    [Test]
    public void TriggerForceUpdateAsync_ShouldRejectUnknownAgent()
    {
        var agent = CreateAgent(version: "1.0.0");
        var release = CreateRelease("1.2.0");
        var policy = CreatePolicy(rolloutPercentage: 100);
        var service = CreateService(agent, policy, [release]);

        Assert.That(async () => await service.TriggerForceUpdateAsync(
                Guid.NewGuid(),
                new ForceAgentUpdateRequest(),
                "tester",
                CancellationToken.None),
            Throws.TypeOf<KeyNotFoundException>());
    }

    [Test]
    public async Task GetRolloutDashboardAsync_ShouldAggregateSnapshotsByClientSiteAndStatus()
    {
        var agent = CreateAgent(version: "1.0.0");
        var release = CreateRelease("1.2.0");
        var policy = CreatePolicy(rolloutPercentage: 100);
        var clientA = Guid.NewGuid();
        var siteA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        var eventRepository = new FakeAgentUpdateEventRepository();
        eventRepository.Snapshots.AddRange(
        [
            new AgentUpdateRolloutAgentSnapshotDto(
                Guid.NewGuid(),
                "agent-a1",
                "Agent A1",
                AgentStatus.Online,
                "1.0.0",
                clientA,
                "Client A",
                siteA,
                "Site A",
                release.Id,
                AgentUpdateEventType.InstallSucceeded,
                "1.2.0",
                "ok",
                DateTime.UtcNow.AddMinutes(-5)),
            new AgentUpdateRolloutAgentSnapshotDto(
                Guid.NewGuid(),
                "agent-a2",
                null,
                AgentStatus.Online,
                "1.0.0",
                clientA,
                "Client A",
                siteA,
                "Site A",
                release.Id,
                AgentUpdateEventType.InstallFailed,
                "1.2.0",
                "boom",
                DateTime.UtcNow.AddMinutes(-4)),
            new AgentUpdateRolloutAgentSnapshotDto(
                Guid.NewGuid(),
                "agent-b1",
                null,
                AgentStatus.Offline,
                "1.0.0",
                clientB,
                "Client B",
                siteB,
                "Site B",
                null,
                null,
                null,
                null,
                null)
        ]);

        var service = CreateService(agent, policy, [release], eventRepository: eventRepository);

        var dashboard = await service.GetRolloutDashboardAsync(limit: 10, cancellationToken: CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(dashboard.Limit, Is.EqualTo(10));
            Assert.That(dashboard.Summary.TotalAgents, Is.EqualTo(3));
            Assert.That(dashboard.Summary.Succeeded, Is.EqualTo(1));
            Assert.That(dashboard.Summary.Failed, Is.EqualTo(1));
            Assert.That(dashboard.Summary.NotStarted, Is.EqualTo(1));
            Assert.That(dashboard.Clients, Has.Count.EqualTo(2));
            Assert.That(dashboard.Sites, Has.Count.EqualTo(2));
            Assert.That(dashboard.Agents.Select(item => item.RolloutStatus), Is.EquivalentTo(new[] { "succeeded", "failed", "not-started" }));
        });

        var clientSummary = dashboard.Clients.Single(item => item.Id == clientA);
        Assert.Multiple(() =>
        {
            Assert.That(clientSummary.Summary.TotalAgents, Is.EqualTo(2));
            Assert.That(clientSummary.Summary.Succeeded, Is.EqualTo(1));
            Assert.That(clientSummary.Summary.Failed, Is.EqualTo(1));
        });
    }

    private static AgentUpdateService CreateService(
        Agent agent,
        AgentUpdatePolicy policy,
        IReadOnlyList<AgentRelease> releases,
        FakeAgentRepository? agentRepository = null,
        FakeAgentUpdateEventRepository? eventRepository = null,
        FakeObjectStorageProviderFactory? storageFactory = null,
        FakeAgentReleaseRepository? releaseRepository = null,
        FakeAgentUpdateBuildRepository? buildRepository = null,
        FakeAgentCommandDispatcher? commandDispatcher = null)
    {
        var seededBuildRepository = buildRepository ?? new FakeAgentUpdateBuildRepository(
            releases
                .SelectMany(release => release.Artifacts.Select(artifact => new AgentUpdateBuild
                {
                    Id = Guid.NewGuid(),
                    Version = release.Version,
                    Platform = artifact.Platform,
                    Architecture = artifact.Architecture,
                    ArtifactType = artifact.ArtifactType,
                    FileName = artifact.FileName,
                    ContentType = artifact.ContentType,
                    StorageObjectKey = artifact.StorageObjectKey,
                    StorageBucket = artifact.StorageBucket,
                    StorageProviderType = artifact.StorageProviderType,
                    Sha256 = artifact.Sha256,
                    SizeBytes = artifact.SizeBytes,
                    SignatureThumbprint = artifact.SignatureThumbprint,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = release.PublishedAtUtc
                }))
                .ToList());

        return new AgentUpdateService(
            agentRepository ?? new FakeAgentRepository(agent),
            new FakeConfigurationResolver(policy, agent.SiteId),
            releaseRepository ?? new FakeAgentReleaseRepository(releases),
            seededBuildRepository,
            eventRepository ?? new FakeAgentUpdateEventRepository(),
            commandDispatcher ?? new FakeAgentCommandDispatcher(),
            storageFactory ?? new FakeObjectStorageProviderFactory(),
            NullLogger<AgentUpdateService>.Instance);
    }

    private static Agent CreateAgent(string version)
    {
        return new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = Guid.NewGuid(),
            Hostname = "agent-01",
            OperatingSystem = "Windows 11",
            AgentVersion = version
        };
    }

    private static AgentRelease CreateRelease(string version, string channel = "stable")
    {
        var releaseId = Guid.NewGuid();
        return new AgentRelease
        {
            Id = releaseId,
            Version = version,
            Channel = channel,
            IsActive = true,
            PublishedAtUtc = DateTime.UtcNow,
            Artifacts =
            [
                new AgentReleaseArtifact
                {
                    Id = Guid.NewGuid(),
                    AgentReleaseId = releaseId,
                    Platform = "windows",
                    Architecture = "amd64",
                    ArtifactType = AgentReleaseArtifactType.Portable,
                    FileName = "discovery-installer.exe",
                    ContentType = "application/x-msdownload",
                    StorageObjectKey = $"agent-updates/{channel}/{version}/windows/amd64/portable/discovery-installer.exe",
                    StorageBucket = "updates",
                    Sha256 = "abc123",
                    SizeBytes = 4096
                }
            ]
        };
    }

    private static AgentUpdatePolicy CreatePolicy(int rolloutPercentage)
    {
        return new AgentUpdatePolicy
        {
            Enabled = true,
            Channel = "stable",
            PreferredArtifactType = AgentReleaseArtifactType.Portable,
            RolloutPercentage = rolloutPercentage
        };
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        private readonly Agent _agent;

        public FakeAgentRepository(Agent agent)
        {
            _agent = agent;
        }

        public int UpdateCalls { get; private set; }

        public Task<Agent?> GetByIdAsync(Guid id)
            => Task.FromResult(id == _agent.Id ? _agent : null);

        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotSupportedException();
        public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();

        public Task UpdateAsync(Agent agent)
        {
            UpdateCalls++;
            return Task.CompletedTask;
        }

        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeConfigurationResolver : IConfigurationResolver
    {
        private readonly ResolvedConfiguration _resolvedConfiguration;

        public FakeConfigurationResolver(AgentUpdatePolicy policy, Guid siteId)
        {
            _resolvedConfiguration = new ResolvedConfiguration
            {
                SiteId = siteId,
                AgentUpdate = policy
            };
        }

        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId)
            => Task.FromResult(_resolvedConfiguration);

        public Task<ServerConfiguration> GetServerAsync() => throw new NotSupportedException();
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<AutoUpdateSettings> GetAutoUpdateSettingsAsync(string level, Guid? targetId = null) => throw new NotSupportedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => throw new NotSupportedException();
        public void ClearCache() { }
    }

    private sealed class FakeAgentReleaseRepository : IAgentReleaseRepository
    {
        private readonly List<AgentRelease> _releases;

        public FakeAgentReleaseRepository(IReadOnlyList<AgentRelease> releases)
        {
            _releases = releases.ToList();
        }

        public Task<IReadOnlyList<AgentRelease>> ListAsync(bool includeInactive = false, string? channel = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<AgentRelease> releases = _releases
                .Where(release => includeInactive || release.IsActive)
                .Where(release => string.IsNullOrWhiteSpace(channel) || release.Channel == channel)
                .ToList();

            return Task.FromResult(releases);
        }

        public Task<AgentRelease?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(_releases.SingleOrDefault(release => release.Id == id));

        public Task<AgentRelease?> GetByVersionAsync(string version, string channel, CancellationToken cancellationToken = default)
            => Task.FromResult(_releases.SingleOrDefault(release => release.Version == version && release.Channel == channel));

        public Task<AgentReleaseArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken cancellationToken = default)
            => Task.FromResult(_releases.SelectMany(release => release.Artifacts).SingleOrDefault(artifact => artifact.Id == artifactId));

        public Task<AgentReleaseArtifact?> GetArtifactAsync(Guid releaseId, string platform, string architecture, AgentReleaseArtifactType artifactType, CancellationToken cancellationToken = default)
        {
            var artifact = _releases
                .Where(release => release.Id == releaseId)
                .SelectMany(release => release.Artifacts)
                .SingleOrDefault(artifact =>
                    artifact.Platform == platform &&
                    artifact.Architecture == architecture &&
                    artifact.ArtifactType == artifactType);

            return Task.FromResult(artifact);
        }

        public Task<AgentRelease> CreateAsync(AgentRelease release, CancellationToken cancellationToken = default)
        {
            if (release.Id == Guid.Empty)
                release.Id = Guid.NewGuid();

            _releases.Add(release);
            return Task.FromResult(release);
        }

        public Task UpdateAsync(AgentRelease release, CancellationToken cancellationToken = default)
        {
            var existing = _releases.SingleOrDefault(item => item.Id == release.Id);
            if (existing is null)
                return Task.CompletedTask;

            existing.Version = release.Version;
            existing.Channel = release.Channel;
            existing.IsActive = release.IsActive;
            existing.Mandatory = release.Mandatory;
            existing.MinimumSupportedVersion = release.MinimumSupportedVersion;
            existing.ReleaseNotes = release.ReleaseNotes;
            existing.PublishedAtUtc = release.PublishedAtUtc;
            existing.CreatedBy = release.CreatedBy;
            existing.UpdatedBy = release.UpdatedBy;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _releases.RemoveAll(release => release.Id == id);
            return Task.CompletedTask;
        }

        public Task<AgentReleaseArtifact> CreateArtifactAsync(AgentReleaseArtifact artifact, CancellationToken cancellationToken = default)
        {
            if (artifact.Id == Guid.Empty)
                artifact.Id = Guid.NewGuid();

            var release = _releases.Single(item => item.Id == artifact.AgentReleaseId);
            release.Artifacts.Add(artifact);
            return Task.FromResult(artifact);
        }

        public Task UpdateArtifactAsync(AgentReleaseArtifact artifact, CancellationToken cancellationToken = default)
        {
            var release = _releases.SingleOrDefault(item => item.Id == artifact.AgentReleaseId);
            var existing = release?.Artifacts.SingleOrDefault(item => item.Id == artifact.Id);
            if (existing is null)
                return Task.CompletedTask;

            existing.Platform = artifact.Platform;
            existing.Architecture = artifact.Architecture;
            existing.ArtifactType = artifact.ArtifactType;
            existing.FileName = artifact.FileName;
            existing.ContentType = artifact.ContentType;
            existing.StorageObjectKey = artifact.StorageObjectKey;
            existing.StorageBucket = artifact.StorageBucket;
            existing.StorageProviderType = artifact.StorageProviderType;
            existing.Sha256 = artifact.Sha256;
            existing.SizeBytes = artifact.SizeBytes;
            existing.SignatureThumbprint = artifact.SignatureThumbprint;
            return Task.CompletedTask;
        }

        public Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
        {
            foreach (var release in _releases)
            {
                release.Artifacts.RemoveAll(artifact => artifact.Id == artifactId);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgentUpdateEventRepository : IAgentUpdateEventRepository
    {
        public List<AgentUpdateEvent> Events { get; } = [];
        public List<AgentUpdateRolloutAgentSnapshotDto> Snapshots { get; } = [];

        public Task<AgentUpdateEvent> CreateAsync(AgentUpdateEvent updateEvent, CancellationToken cancellationToken = default)
        {
            if (updateEvent.Id == Guid.Empty)
                updateEvent.Id = Guid.NewGuid();

            Events.Add(updateEvent);
            return Task.FromResult(updateEvent);
        }

        public Task<IReadOnlyList<AgentUpdateEvent>> GetByAgentIdAsync(Guid agentId, int limit = 100, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentUpdateEvent>>(Events.Where(item => item.AgentId == agentId).Take(limit).ToList());

        public Task<IReadOnlyList<AgentUpdateRolloutAgentSnapshotDto>> GetRolloutSnapshotsAsync(Guid? clientId = null, Guid? siteId = null, int limit = 200, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<AgentUpdateRolloutAgentSnapshotDto> result = Snapshots
                .Where(item => !clientId.HasValue || item.ClientId == clientId.Value)
                .Where(item => !siteId.HasValue || item.SiteId == siteId.Value)
                .Take(limit)
                .ToList();

            return Task.FromResult(result);
        }
    }

    private sealed class FakeAgentUpdateBuildRepository : IAgentUpdateBuildRepository
    {
        private readonly List<AgentUpdateBuild> _builds;

        public FakeAgentUpdateBuildRepository(IReadOnlyList<AgentUpdateBuild> builds)
        {
            _builds = builds.ToList();
        }

        public Task<AgentUpdateBuild?> GetCurrentAsync(string platform, string architecture, AgentReleaseArtifactType artifactType, CancellationToken cancellationToken = default)
        {
            var build = _builds
                .Where(item => item.IsActive)
                .Where(item => item.Platform == platform)
                .Where(item => item.Architecture == architecture)
                .Where(item => item.ArtifactType == artifactType)
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.CreatedAt)
                .FirstOrDefault();

            return Task.FromResult(build);
        }

        public Task<AgentUpdateBuild> CreateAsync(AgentUpdateBuild build, CancellationToken cancellationToken = default)
        {
            if (build.Id == Guid.Empty)
                build.Id = Guid.NewGuid();

            build.CreatedAt = DateTime.UtcNow;
            build.UpdatedAt = build.CreatedAt;
            _builds.Add(build);
            return Task.FromResult(build);
        }

        public Task DeactivateCurrentAsync(string platform, string architecture, AgentReleaseArtifactType artifactType, Guid keepActiveBuildId, CancellationToken cancellationToken = default)
        {
            foreach (var build in _builds.Where(item => item.Platform == platform && item.Architecture == architecture && item.ArtifactType == artifactType && item.Id != keepActiveBuildId))
            {
                build.IsActive = false;
                build.UpdatedAt = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgentCommandDispatcher : IAgentCommandDispatcher
    {
        public List<AgentCommand> Commands { get; } = [];

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            if (command.Id == Guid.Empty)
                command.Id = Guid.NewGuid();

            command.CreatedAt = DateTime.UtcNow;
            command.Status = CommandStatus.Sent;
            command.SentAt = command.CreatedAt;
            Commands.Add(new AgentCommand
            {
                Id = command.Id,
                AgentId = command.AgentId,
                CommandType = command.CommandType,
                Payload = command.Payload,
                Status = command.Status,
                CreatedAt = command.CreatedAt,
                SentAt = command.SentAt
            });

            return Task.FromResult(command);
        }
    }

    private sealed class FakeObjectStorageProviderFactory : IObjectStorageProviderFactory
    {
        private readonly IObjectStorageService _storageService;

        public FakeObjectStorageProviderFactory()
        {
            _storageService = new FakeObjectStorageService("https://example.invalid/download");
        }

        public FakeObjectStorageProviderFactory(IObjectStorageService storageService)
        {
            _storageService = storageService;
        }

        public IObjectStorageService CreateObjectStorageService() => _storageService;
        public Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default) => Task.FromResult(_storageService);
        public Task<List<string>> ValidateConfigurationAsync() => throw new NotSupportedException();
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeObjectStorageService : IObjectStorageService
    {
        private readonly string _downloadUrl;
        private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _objects = new(StringComparer.Ordinal);

        public FakeObjectStorageService(string downloadUrl)
        {
            _downloadUrl = downloadUrl;
        }

        public string? LastObjectKey { get; private set; }
        public int? LastTtlHours { get; private set; }
        public List<string> DownloadedKeys { get; } = [];
        public List<string> UploadedKeys { get; } = [];

        public void SeedObject(string objectKey, string contentType, byte[] content)
        {
            _objects[objectKey] = (content, contentType);
        }

        public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default)
        {
            LastObjectKey = objectKey;
            LastTtlHours = ttlHours;
            return Task.FromResult(_downloadUrl);
        }

        public async Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            _objects[objectKey] = (bytes, contentType);
            UploadedKeys.Add(objectKey);

            return new StorageObject
            {
                ObjectKey = objectKey,
                Bucket = "updates",
                ContentType = contentType,
                SizeBytes = bytes.Length,
                StorageProvider = ObjectStorageProviderType.MinIO,
                StoredAt = DateTime.UtcNow
            };
        }

        public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            DownloadedKeys.Add(objectKey);
            var data = _objects[objectKey].Bytes;
            return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
        }
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            _objects.Remove(objectKey);
            return Task.CompletedTask;
        }
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}