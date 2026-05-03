using System.IO;
using System.Text.Json;
using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public sealed record AgentUpdateManifestRequest(
    string? CurrentVersion,
    string? Platform,
    string? Architecture,
    AgentReleaseArtifactType? ArtifactType);

public sealed class AgentUpdateManifestDto
{
    public Guid? ReleaseId { get; set; }
    public string Revision { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Channel { get; set; } = "stable";
    public string? CurrentVersion { get; set; }
    public bool CurrentVersionValid { get; set; }
    public string? LatestVersion { get; set; }
    public string? MinimumRequiredVersion { get; set; }
    public string? MinimumSupportedVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool Mandatory { get; set; }
    public bool RolloutEligible { get; set; }
    public bool DirectUpdateSupported { get; set; }
    public string? Platform { get; set; }
    public string? Architecture { get; set; }
    public AgentReleaseArtifactType? ArtifactType { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public string? Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public string? SignatureThumbprint { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public string? ReleaseNotes { get; set; }
    public string? Message { get; set; }
}

public sealed record AgentUpdateDownloadRequest(
    Guid? ReleaseId,
    string? Version,
    string? Platform,
    string? Architecture,
    AgentReleaseArtifactType? ArtifactType);

public sealed class AgentUpdateRedirectPayload
{
    public required string DownloadUrl { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required string Platform { get; init; }
    public required string Architecture { get; init; }
    public required AgentReleaseArtifactType ArtifactType { get; init; }
}

/// <summary>
/// Resultado da sincronização do repositório do agent.
/// </summary>
public sealed record AgentRepositorySyncResult(
    string Branch,
    string? BeforeCommit,
    string AfterCommit,
    bool Changed,
    string? GitMessage);

public sealed record AgentUpdateReportRequest(
    AgentUpdateEventType EventType,
    Guid? ReleaseId,
    string? CurrentVersion,
    string? TargetVersion,
    string? Message,
    string? CorrelationId,
    DateTime? OccurredAtUtc,
    JsonElement? Details);

public sealed record AgentReleaseWriteRequest(
    string Version,
    string? Channel,
    bool IsActive,
    bool Mandatory,
    string? MinimumSupportedVersion,
    string? ReleaseNotes);

public sealed record PromoteAgentReleaseRequest(
    string? TargetChannel = "stable",
    bool IsActive = true);

public sealed record ForceAgentUpdateRequest(
    string? Reason = null);

public sealed record AgentUpdateRolloutAgentSnapshotDto(
    Guid AgentId,
    string Hostname,
    string? DisplayName,
    AgentStatus AgentStatus,
    string? CurrentVersion,
    Guid ClientId,
    string ClientName,
    Guid SiteId,
    string SiteName,
    Guid? ReleaseId,
    AgentUpdateEventType? LatestEventType,
    string? TargetVersion,
    string? Message,
    DateTime? LastEventAtUtc);

public sealed record AgentUpdateRolloutSummaryDto(
    int TotalAgents,
    int NotStarted,
    int Checking,
    int UpdateAvailable,
    int Downloading,
    int Installing,
    int Succeeded,
    int Failed,
    int Deferred,
    int Rollback);

public sealed record AgentUpdateRolloutScopeSummaryDto(
    Guid Id,
    string Name,
    AgentUpdateRolloutSummaryDto Summary);

public sealed record AgentUpdateRolloutAgentDto(
    Guid AgentId,
    string Hostname,
    string? DisplayName,
    AgentStatus AgentStatus,
    string? CurrentVersion,
    Guid ClientId,
    string ClientName,
    Guid SiteId,
    string SiteName,
    Guid? ReleaseId,
    string? TargetVersion,
    AgentUpdateEventType? LatestEventType,
    string RolloutStatus,
    string? Message,
    DateTime? LastEventAtUtc);

public sealed record AgentUpdateRolloutDashboardDto(
    Guid? ClientId,
    Guid? SiteId,
    int Limit,
    AgentUpdateRolloutSummaryDto Summary,
    IReadOnlyList<AgentUpdateRolloutScopeSummaryDto> Clients,
    IReadOnlyList<AgentUpdateRolloutScopeSummaryDto> Sites,
    IReadOnlyList<AgentUpdateRolloutAgentDto> Agents,
    DateTime GeneratedAtUtc);
