using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AgentUpdateService(
    IAgentRepository agentRepository,
    IConfigurationResolver configurationResolver,
    IAgentReleaseRepository agentReleaseRepository,
    IAgentUpdateEventRepository agentUpdateEventRepository,
    IAgentCommandDispatcher agentCommandDispatcher,
    IObjectStorageProviderFactory storageProviderFactory,
    ILogger<AgentUpdateService> logger) : IAgentUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<AgentRelease>> ListReleasesAsync(bool includeInactive = false, string? channel = null, CancellationToken cancellationToken = default)
        => agentReleaseRepository.ListAsync(includeInactive, NormalizeChannel(channel), cancellationToken);

    public Task<AgentRelease?> GetReleaseAsync(Guid releaseId, CancellationToken cancellationToken = default)
        => agentReleaseRepository.GetByIdAsync(releaseId, cancellationToken);

    public async Task<AgentRelease> CreateReleaseAsync(AgentReleaseWriteRequest request, string? actor = null, CancellationToken cancellationToken = default)
    {
        var version = NormalizeAndValidateVersion(request.Version, nameof(request.Version));
        var channel = NormalizeChannel(request.Channel);
        var minimumSupportedVersion = NormalizeOptionalVersion(request.MinimumSupportedVersion, nameof(request.MinimumSupportedVersion));

        var existing = await agentReleaseRepository.GetByVersionAsync(version, channel, cancellationToken);
        if (existing is not null)
            throw new InvalidOperationException($"Release {version} already exists for channel '{channel}'.");

        var release = new AgentRelease
        {
            Version = version,
            Channel = channel,
            IsActive = request.IsActive,
            Mandatory = request.Mandatory,
            MinimumSupportedVersion = minimumSupportedVersion,
            ReleaseNotes = string.IsNullOrWhiteSpace(request.ReleaseNotes) ? null : request.ReleaseNotes.Trim(),
            PublishedAtUtc = DateTime.UtcNow,
            CreatedBy = actor,
            UpdatedBy = actor
        };

        return await agentReleaseRepository.CreateAsync(release, cancellationToken);
    }

    public async Task<AgentRelease> UpdateReleaseAsync(Guid releaseId, AgentReleaseWriteRequest request, string? actor = null, CancellationToken cancellationToken = default)
    {
        var existing = await agentReleaseRepository.GetByIdAsync(releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");

        var version = NormalizeAndValidateVersion(request.Version, nameof(request.Version));
        var channel = NormalizeChannel(request.Channel);
        var minimumSupportedVersion = NormalizeOptionalVersion(request.MinimumSupportedVersion, nameof(request.MinimumSupportedVersion));

        var duplicate = await agentReleaseRepository.GetByVersionAsync(version, channel, cancellationToken);
        if (duplicate is not null && duplicate.Id != releaseId)
            throw new InvalidOperationException($"Release {version} already exists for channel '{channel}'.");

        existing.Version = version;
        existing.Channel = channel;
        existing.IsActive = request.IsActive;
        existing.Mandatory = request.Mandatory;
        existing.MinimumSupportedVersion = minimumSupportedVersion;
        existing.ReleaseNotes = string.IsNullOrWhiteSpace(request.ReleaseNotes) ? null : request.ReleaseNotes.Trim();
        existing.UpdatedBy = actor;
        existing.PublishedAtUtc = DateTime.UtcNow;

        await agentReleaseRepository.UpdateAsync(existing, cancellationToken);
        return (await agentReleaseRepository.GetByIdAsync(releaseId, cancellationToken))!;
    }

    public async Task<AgentRelease> PromoteReleaseAsync(Guid releaseId, PromoteAgentReleaseRequest request, string? actor = null, CancellationToken cancellationToken = default)
    {
        var sourceRelease = await agentReleaseRepository.GetByIdAsync(releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");

        var targetChannel = NormalizeChannel(request.TargetChannel);
        if (string.Equals(sourceRelease.Channel, targetChannel, StringComparison.Ordinal))
            throw new InvalidOperationException("Source and target channels must be different for promotion.");

        var duplicate = await agentReleaseRepository.GetByVersionAsync(sourceRelease.Version, targetChannel, cancellationToken);
        if (duplicate is not null)
            throw new InvalidOperationException($"Release {sourceRelease.Version} already exists for channel '{targetChannel}'.");

        var promotedRelease = await agentReleaseRepository.CreateAsync(new AgentRelease
        {
            Version = sourceRelease.Version,
            Channel = targetChannel,
            IsActive = request.IsActive,
            Mandatory = sourceRelease.Mandatory,
            MinimumSupportedVersion = sourceRelease.MinimumSupportedVersion,
            ReleaseNotes = sourceRelease.ReleaseNotes,
            PublishedAtUtc = DateTime.UtcNow,
            CreatedBy = actor,
            UpdatedBy = actor
        }, cancellationToken);

        if (sourceRelease.Artifacts.Count == 0)
            return promotedRelease;

        var storage = await storageProviderFactory.CreateObjectStorageServiceAsync(cancellationToken);

        try
        {
            foreach (var artifact in sourceRelease.Artifacts)
            {
                await using var content = await storage.DownloadAsync(artifact.StorageObjectKey, cancellationToken);
                await UploadArtifactAsync(
                    promotedRelease.Id,
                    artifact.Platform,
                    artifact.Architecture,
                    artifact.ArtifactType,
                    artifact.FileName,
                    artifact.ContentType,
                    content,
                    artifact.SignatureThumbprint,
                    cancellationToken);
            }
        }
        catch
        {
            await DeleteReleaseAsync(promotedRelease.Id, cancellationToken);
            throw;
        }

        return (await agentReleaseRepository.GetByIdAsync(promotedRelease.Id, cancellationToken))!;
    }

    public async Task DeleteReleaseAsync(Guid releaseId, CancellationToken cancellationToken = default)
    {
        var release = await agentReleaseRepository.GetByIdAsync(releaseId, cancellationToken);
        if (release is null)
            return;

        var storage = await storageProviderFactory.CreateObjectStorageServiceAsync(cancellationToken);
        foreach (var artifact in release.Artifacts.Where(a => !string.IsNullOrWhiteSpace(a.StorageObjectKey)))
            await storage.DeleteAsync(artifact.StorageObjectKey, cancellationToken);

        await agentReleaseRepository.DeleteAsync(releaseId, cancellationToken);
    }

    public async Task<AgentReleaseArtifact> UploadArtifactAsync(
        Guid releaseId,
        string platform,
        string architecture,
        AgentReleaseArtifactType artifactType,
        string fileName,
        string contentType,
        Stream content,
        string? signatureThumbprint = null,
        CancellationToken cancellationToken = default)
    {
        var release = await agentReleaseRepository.GetByIdAsync(releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Release {releaseId} not found.");

        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedArchitecture = NormalizeArchitecture(architecture);
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
            throw new InvalidOperationException("Artifact fileName is required.");

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var objectKey = BuildArtifactObjectKey(release, normalizedPlatform, normalizedArchitecture, artifactType, safeFileName);
        var storage = await storageProviderFactory.CreateObjectStorageServiceAsync(cancellationToken);
        await using var uploadStream = new MemoryStream(bytes, writable: false);
        var storageObject = await storage.UploadAsync(
            objectKey,
            uploadStream,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            cancellationToken);

        var existing = await agentReleaseRepository.GetArtifactAsync(
            releaseId,
            normalizedPlatform,
            normalizedArchitecture,
            artifactType,
            cancellationToken);

        if (existing is not null && !string.Equals(existing.StorageObjectKey, storageObject.ObjectKey, StringComparison.Ordinal))
            await storage.DeleteAsync(existing.StorageObjectKey, cancellationToken);

        var artifact = existing ?? new AgentReleaseArtifact { AgentReleaseId = releaseId };
        artifact.Platform = normalizedPlatform;
        artifact.Architecture = normalizedArchitecture;
        artifact.ArtifactType = artifactType;
        artifact.FileName = safeFileName;
        artifact.ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        artifact.StorageObjectKey = storageObject.ObjectKey;
        artifact.StorageBucket = storageObject.Bucket;
        artifact.StorageProviderType = (int)storageObject.StorageProvider;
        artifact.Sha256 = sha256;
        artifact.SizeBytes = storageObject.SizeBytes;
        artifact.SignatureThumbprint = string.IsNullOrWhiteSpace(signatureThumbprint) ? null : signatureThumbprint.Trim();

        if (existing is null)
            return await agentReleaseRepository.CreateArtifactAsync(artifact, cancellationToken);

        await agentReleaseRepository.UpdateArtifactAsync(artifact, cancellationToken);
        return (await agentReleaseRepository.GetArtifactByIdAsync(artifact.Id, cancellationToken))!;
    }

    public async Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await agentReleaseRepository.GetArtifactByIdAsync(artifactId, cancellationToken);
        if (artifact is null)
            return;

        var storage = await storageProviderFactory.CreateObjectStorageServiceAsync(cancellationToken);
        await storage.DeleteAsync(artifact.StorageObjectKey, cancellationToken);
        await agentReleaseRepository.DeleteArtifactAsync(artifactId, cancellationToken);
    }

    public Task<IReadOnlyList<AgentUpdateEvent>> GetEventsByAgentAsync(Guid agentId, int limit = 100, CancellationToken cancellationToken = default)
        => agentUpdateEventRepository.GetByAgentIdAsync(agentId, limit, cancellationToken);

    public async Task<AgentUpdateRolloutDashboardDto> GetRolloutDashboardAsync(Guid? clientId = null, Guid? siteId = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var snapshots = await agentUpdateEventRepository.GetRolloutSnapshotsAsync(clientId, siteId, safeLimit, cancellationToken);
        var agents = snapshots.Select(MapRolloutAgent).ToList();

        return new AgentUpdateRolloutDashboardDto(
            clientId,
            siteId,
            safeLimit,
            BuildRolloutSummary(agents),
            agents
                .GroupBy(agent => new { agent.ClientId, agent.ClientName })
                .OrderBy(group => group.Key.ClientName)
                .Select(group => new AgentUpdateRolloutScopeSummaryDto(
                    group.Key.ClientId,
                    group.Key.ClientName,
                    BuildRolloutSummary(group)))
                .ToList(),
            agents
                .GroupBy(agent => new { agent.SiteId, agent.SiteName })
                .OrderBy(group => group.Key.SiteName)
                .Select(group => new AgentUpdateRolloutScopeSummaryDto(
                    group.Key.SiteId,
                    group.Key.SiteName,
                    BuildRolloutSummary(group)))
                .ToList(),
            agents,
            DateTime.UtcNow);
    }

    public async Task<AgentUpdateManifestDto> GetManifestAsync(Guid agentId, AgentUpdateManifestRequest request, CancellationToken cancellationToken = default)
    {
        var agent = await agentRepository.GetByIdAsync(agentId)
            ?? throw new KeyNotFoundException($"Agent {agentId} not found.");

        var resolved = await configurationResolver.ResolveForSiteAsync(agent.SiteId);
        var policy = resolved.AgentUpdate ?? new AgentUpdatePolicy();
        policy.Normalize();

        var currentVersionRaw = request.CurrentVersion ?? agent.AgentVersion;
        var normalizedPlatform = NormalizePlatform(request.Platform ?? InferPlatform(agent.OperatingSystem));
        var normalizedArchitecture = NormalizeArchitecture(request.Architecture);
        var artifactType = request.ArtifactType ?? policy.PreferredArtifactType;
        var currentVersionValid = SemanticVersion.TryParse(currentVersionRaw, out var currentVersion);

        var selected = await SelectReleaseAsync(policy, normalizedPlatform, normalizedArchitecture, artifactType, cancellationToken);
        var release = selected.Release;
        var artifact = selected.Artifact;
        var minimumRequiredVersion = MaxVersion(policy.MinimumRequiredVersion, release?.MinimumSupportedVersion);
        var mandatory = IsMandatory(currentVersionValid, currentVersion, minimumRequiredVersion, release);
        var rolloutEligible = mandatory || IsAgentEligibleForRollout(agentId, policy.RolloutPercentage);
        var updateAvailable = release is not null
            && (!currentVersionValid || currentVersion.CompareTo(selected.Version) < 0);

        var revision = ComputeRevision(policy, release, artifact);

        return new AgentUpdateManifestDto
        {
            ReleaseId = release?.Id,
            Revision = revision,
            Enabled = policy.Enabled,
            Channel = policy.Channel,
            CurrentVersion = currentVersionRaw,
            CurrentVersionValid = currentVersionValid,
            LatestVersion = release?.Version,
            MinimumRequiredVersion = policy.MinimumRequiredVersion,
            MinimumSupportedVersion = release?.MinimumSupportedVersion,
            UpdateAvailable = updateAvailable,
            Mandatory = mandatory,
            RolloutEligible = rolloutEligible,
            DirectUpdateSupported = policy.Enabled && updateAvailable && rolloutEligible && artifact is not null,
            Platform = artifact?.Platform ?? normalizedPlatform,
            Architecture = artifact?.Architecture ?? normalizedArchitecture,
            ArtifactType = artifact?.ArtifactType ?? artifactType,
            FileName = artifact?.FileName,
            ContentType = artifact?.ContentType,
            Sha256 = artifact?.Sha256,
            SizeBytes = artifact?.SizeBytes,
            SignatureThumbprint = artifact?.SignatureThumbprint,
            PublishedAtUtc = release?.PublishedAtUtc,
            ReleaseNotes = release?.ReleaseNotes,
            Message = BuildManifestMessage(policy, release, updateAvailable, rolloutEligible, currentVersionValid)
        };
    }

    public async Task<AgentCommand> TriggerForceCheckAsync(Guid agentId, ForceAgentUpdateCheckRequest request, string? actor = null, CancellationToken cancellationToken = default)
    {
        _ = await agentRepository.GetByIdAsync(agentId)
            ?? throw new KeyNotFoundException($"Agent {agentId} not found.");

        var channel = string.IsNullOrWhiteSpace(request.Channel) ? null : NormalizeChannel(request.Channel);
        var targetVersion = NormalizeOptionalVersion(request.TargetVersion, nameof(request.TargetVersion), allowNullWhenEmpty: true);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > 200 })
            throw new InvalidOperationException("Reason cannot exceed 200 characters.");

        var requestedAtUtc = DateTime.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            action = "check-update",
            force = true,
            requestedAtUtc,
            requestedBy = string.IsNullOrWhiteSpace(actor) ? "api" : actor.Trim(),
            channel,
            targetVersion,
            artifactType = request.ArtifactType?.ToString(),
            reason
        }, JsonOptions);

        return await agentCommandDispatcher.DispatchAsync(new AgentCommand
        {
            AgentId = agentId,
            CommandType = CommandType.Update,
            Payload = payload
        }, cancellationToken);
    }
    public async Task<AgentUpdateRedirectPayload?> GetPresignedDownloadUrlAsync(Guid agentId, AgentUpdateDownloadRequest request, CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(
            agentId,
            new AgentUpdateManifestRequest(
                CurrentVersion: null,
                Platform: request.Platform,
                Architecture: request.Architecture,
                ArtifactType: request.ArtifactType),
            cancellationToken);

        if (!manifest.DirectUpdateSupported || !manifest.ReleaseId.HasValue)
            return null;

        if (request.ReleaseId.HasValue && request.ReleaseId.Value != manifest.ReleaseId.Value)
            return null;

        if (!string.IsNullOrWhiteSpace(request.Version) &&
            !string.Equals(NormalizeOptionalVersion(request.Version, nameof(request.Version)), manifest.LatestVersion, StringComparison.Ordinal))
            return null;

        var artifact = await agentReleaseRepository.GetArtifactAsync(
            manifest.ReleaseId.Value,
            NormalizePlatform(request.Platform ?? manifest.Platform),
            NormalizeArchitecture(request.Architecture ?? manifest.Architecture),
            request.ArtifactType ?? manifest.ArtifactType ?? AgentReleaseArtifactType.Portable,
            cancellationToken);

        if (artifact is null)
            return null;

        var storage = await storageProviderFactory.CreateObjectStorageServiceAsync(cancellationToken);
        var downloadUrl = await storage.GetPresignedDownloadUrlAsync(artifact.StorageObjectKey, ttlHours: 1, cancellationToken);

        return new AgentUpdateRedirectPayload
        {
            DownloadUrl = downloadUrl,
            FileName = artifact.FileName,
            ContentType = artifact.ContentType,
            Sha256 = artifact.Sha256,
            SizeBytes = artifact.SizeBytes,
            Platform = artifact.Platform,
            Architecture = artifact.Architecture,
            ArtifactType = artifact.ArtifactType
        };
    }

    public async Task<AgentUpdateEvent> RecordEventAsync(Guid agentId, AgentUpdateReportRequest request, CancellationToken cancellationToken = default)
    {
        var agent = await agentRepository.GetByIdAsync(agentId)
            ?? throw new KeyNotFoundException($"Agent {agentId} not found.");

        var normalizedCurrentVersion = NormalizeOptionalVersion(request.CurrentVersion, nameof(request.CurrentVersion), allowNullWhenEmpty: true);
        var normalizedTargetVersion = NormalizeOptionalVersion(request.TargetVersion, nameof(request.TargetVersion), allowNullWhenEmpty: true);

        var updateEvent = new AgentUpdateEvent
        {
            AgentId = agentId,
            AgentReleaseId = request.ReleaseId,
            EventType = request.EventType,
            CurrentVersion = normalizedCurrentVersion,
            TargetVersion = normalizedTargetVersion,
            Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
            CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? null : request.CorrelationId.Trim(),
            DetailsJson = request.Details?.ValueKind is null or JsonValueKind.Null ? null : request.Details.Value.GetRawText(),
            OccurredAtUtc = request.OccurredAtUtc ?? DateTime.UtcNow
        };

        var created = await agentUpdateEventRepository.CreateAsync(updateEvent, cancellationToken);

        if (request.EventType == AgentUpdateEventType.InstallSucceeded && !string.IsNullOrWhiteSpace(normalizedTargetVersion))
        {
            agent.AgentVersion = normalizedTargetVersion;
            await agentRepository.UpdateAsync(agent);
        }

        logger.LogInformation(
            "Agent update event recorded. AgentId={AgentId}, EventType={EventType}, TargetVersion={TargetVersion}",
            agentId,
            request.EventType,
            normalizedTargetVersion);

        return created;
    }

    private async Task<(AgentRelease? Release, AgentReleaseArtifact? Artifact, SemanticVersion Version)> SelectReleaseAsync(
        AgentUpdatePolicy policy,
        string platform,
        string architecture,
        AgentReleaseArtifactType artifactType,
        CancellationToken cancellationToken)
    {
        if (!policy.Enabled)
            return (null, null, default);

        var releases = await agentReleaseRepository.ListAsync(includeInactive: false, policy.Channel, cancellationToken);
        var candidates = releases
            .Select(release => new
            {
                Release = release,
                Artifact = release.Artifacts.FirstOrDefault(artifact =>
                    artifact.Platform == platform &&
                    artifact.Architecture == architecture &&
                    artifact.ArtifactType == artifactType),
                ParsedVersion = SemanticVersion.TryParse(release.Version, out var parsedVersion) ? parsedVersion : default(SemanticVersion),
                VersionValid = SemanticVersion.TryParse(release.Version, out _)
            })
            .Where(item => item.Artifact is not null && item.VersionValid)
            .OrderByDescending(item => item.ParsedVersion)
            .ThenByDescending(item => item.Release.PublishedAtUtc)
            .ToList();

        if (candidates.Count == 0)
            return (null, null, default);

        if (!string.IsNullOrWhiteSpace(policy.TargetVersion))
        {
            var targeted = candidates.FirstOrDefault(item => string.Equals(item.Release.Version, policy.TargetVersion, StringComparison.Ordinal));
            if (targeted is not null)
                return (targeted.Release, targeted.Artifact, targeted.ParsedVersion);
        }

        var selected = candidates[0];
        return (selected.Release, selected.Artifact, selected.ParsedVersion);
    }

    private static bool IsMandatory(bool currentVersionValid, SemanticVersion currentVersion, string? minimumRequiredVersion, AgentRelease? release)
    {
        if (release?.Mandatory == true)
            return true;

        if (string.IsNullOrWhiteSpace(minimumRequiredVersion))
            return false;

        if (!SemanticVersion.TryParse(minimumRequiredVersion, out var minimumVersion))
            return false;

        if (!currentVersionValid)
            return true;

        return currentVersion.CompareTo(minimumVersion) < 0;
    }

    private static bool IsAgentEligibleForRollout(Guid agentId, int rolloutPercentage)
    {
        if (rolloutPercentage >= 100)
            return true;

        if (rolloutPercentage <= 0)
            return false;

        var hash = SHA256.HashData(agentId.ToByteArray());
        var bucket = BitConverter.ToUInt32(hash, 0) % 100;
        return bucket < rolloutPercentage;
    }

    private static AgentUpdateRolloutAgentDto MapRolloutAgent(AgentUpdateRolloutAgentSnapshotDto snapshot)
    {
        return new AgentUpdateRolloutAgentDto(
            snapshot.AgentId,
            snapshot.Hostname,
            snapshot.DisplayName,
            snapshot.AgentStatus,
            snapshot.CurrentVersion,
            snapshot.ClientId,
            snapshot.ClientName,
            snapshot.SiteId,
            snapshot.SiteName,
            snapshot.ReleaseId,
            snapshot.TargetVersion,
            snapshot.LatestEventType,
            MapRolloutStatus(snapshot.LatestEventType),
            snapshot.Message,
            snapshot.LastEventAtUtc);
    }

    private static AgentUpdateRolloutSummaryDto BuildRolloutSummary(IEnumerable<AgentUpdateRolloutAgentDto> agents)
    {
        var materialized = agents.ToList();
        var statusCounts = materialized
            .GroupBy(agent => agent.RolloutStatus, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new AgentUpdateRolloutSummaryDto(
            materialized.Count,
            GetRolloutCount(statusCounts, "not-started"),
            GetRolloutCount(statusCounts, "checking"),
            GetRolloutCount(statusCounts, "update-available"),
            GetRolloutCount(statusCounts, "downloading"),
            GetRolloutCount(statusCounts, "installing"),
            GetRolloutCount(statusCounts, "succeeded"),
            GetRolloutCount(statusCounts, "failed"),
            GetRolloutCount(statusCounts, "deferred"),
            GetRolloutCount(statusCounts, "rollback"));
    }

    private static int GetRolloutCount(IReadOnlyDictionary<string, int> counts, string status)
        => counts.TryGetValue(status, out var value) ? value : 0;

    private static string MapRolloutStatus(AgentUpdateEventType? eventType)
    {
        return eventType switch
        {
            null => "not-started",
            AgentUpdateEventType.CheckStarted or AgentUpdateEventType.CheckCompleted => "checking",
            AgentUpdateEventType.UpdateAvailable => "update-available",
            AgentUpdateEventType.DownloadStarted or AgentUpdateEventType.DownloadCompleted => "downloading",
            AgentUpdateEventType.InstallStarted => "installing",
            AgentUpdateEventType.InstallSucceeded => "succeeded",
            AgentUpdateEventType.DownloadFailed or AgentUpdateEventType.InstallFailed or AgentUpdateEventType.RollbackFailed => "failed",
            AgentUpdateEventType.Deferred => "deferred",
            AgentUpdateEventType.RollbackStarted or AgentUpdateEventType.RollbackSucceeded => "rollback",
            _ => "not-started"
        };
    }

    private static string ComputeRevision(AgentUpdatePolicy policy, AgentRelease? release, AgentReleaseArtifact? artifact)
    {
        var payload = string.Join("|",
            policy.Enabled,
            policy.Channel,
            policy.TargetVersion ?? string.Empty,
            policy.MinimumRequiredVersion ?? string.Empty,
            policy.PreferredArtifactType,
            policy.RolloutPercentage,
            release?.Id.ToString("N") ?? string.Empty,
            release?.Version ?? string.Empty,
            release?.Mandatory ?? false,
            release?.MinimumSupportedVersion ?? string.Empty,
            artifact?.Id.ToString("N") ?? string.Empty,
            artifact?.Sha256 ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"agent-update:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string BuildManifestMessage(AgentUpdatePolicy policy, AgentRelease? release, bool updateAvailable, bool rolloutEligible, bool currentVersionValid)
    {
        if (!policy.Enabled)
            return "Agent self-update is disabled by policy.";

        if (release is null)
            return "No published release matches the requested platform, architecture and channel.";

        if (!currentVersionValid)
            return "Current agent version is missing or invalid; update should be evaluated carefully.";

        if (!updateAvailable)
            return "Agent is already on the latest applicable version.";

        if (!rolloutEligible)
            return "A newer version exists, but this agent is outside the current rollout window.";

        return "A newer agent version is available for download.";
    }

    private static string BuildArtifactObjectKey(AgentRelease release, string platform, string architecture, AgentReleaseArtifactType artifactType, string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        return $"agent-updates/{release.Channel}/{release.Version}/{platform}/{architecture}/{artifactType.ToString().ToLowerInvariant()}/{Guid.NewGuid():N}/{safeFileName}";
    }

    private static string NormalizeChannel(string? channel)
        => string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim().ToLowerInvariant();

    private static string NormalizePlatform(string? platform)
        => string.IsNullOrWhiteSpace(platform) ? "windows" : platform.Trim().ToLowerInvariant();

    private static string NormalizeArchitecture(string? architecture)
        => string.IsNullOrWhiteSpace(architecture) ? "amd64" : architecture.Trim().ToLowerInvariant();

    private static string InferPlatform(string? operatingSystem)
    {
        if (string.IsNullOrWhiteSpace(operatingSystem))
            return "windows";

        var normalized = operatingSystem.Trim().ToLowerInvariant();
        if (normalized.Contains("linux", StringComparison.Ordinal))
            return "linux";
        if (normalized.Contains("mac", StringComparison.Ordinal) || normalized.Contains("darwin", StringComparison.Ordinal))
            return "darwin";
        return "windows";
    }

    private static string NormalizeAndValidateVersion(string rawVersion, string fieldName)
        => NormalizeOptionalVersion(rawVersion, fieldName, allowNullWhenEmpty: false)!;

    private static string? NormalizeOptionalVersion(string? rawVersion, string fieldName, bool allowNullWhenEmpty = true)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            if (allowNullWhenEmpty)
                return null;

            throw new InvalidOperationException($"{fieldName} is required.");
        }

        var normalized = rawVersion.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];

        if (!SemanticVersion.TryParse(normalized, out _))
            throw new InvalidOperationException($"{fieldName} must be a valid semantic version.");

        return normalized;
    }

    private static string? MaxVersion(string? left, string? right)
    {
        var normalizedLeft = NormalizeOptionalVersion(left, nameof(left));
        var normalizedRight = NormalizeOptionalVersion(right, nameof(right));

        if (normalizedLeft is null)
            return normalizedRight;

        if (normalizedRight is null)
            return normalizedLeft;

        return SemanticVersion.TryParse(normalizedLeft, out var leftVersion) &&
               SemanticVersion.TryParse(normalizedRight, out var rightVersion) &&
               leftVersion.CompareTo(rightVersion) >= 0
            ? normalizedLeft
            : normalizedRight;
    }
}
