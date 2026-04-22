using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAgentUpdateService
{
    Task<IReadOnlyList<AgentRelease>> ListReleasesAsync(bool includeInactive = false, string? channel = null, CancellationToken cancellationToken = default);
    Task<AgentRelease?> GetReleaseAsync(Guid releaseId, CancellationToken cancellationToken = default);
    Task<AgentRelease> CreateReleaseAsync(AgentReleaseWriteRequest request, string? actor = null, CancellationToken cancellationToken = default);
    Task<AgentRelease> UpdateReleaseAsync(Guid releaseId, AgentReleaseWriteRequest request, string? actor = null, CancellationToken cancellationToken = default);
    Task<AgentRelease> PromoteReleaseAsync(Guid releaseId, PromoteAgentReleaseRequest request, string? actor = null, CancellationToken cancellationToken = default);
    Task<AgentCommand> TriggerForceCheckAsync(Guid agentId, ForceAgentUpdateCheckRequest request, string? actor = null, CancellationToken cancellationToken = default);
    Task DeleteReleaseAsync(Guid releaseId, CancellationToken cancellationToken = default);
    Task<AgentReleaseArtifact> UploadArtifactAsync(
        Guid releaseId,
        string platform,
        string architecture,
        AgentReleaseArtifactType artifactType,
        string fileName,
        string contentType,
        Stream content,
        string? signatureThumbprint = null,
        CancellationToken cancellationToken = default);
    Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentUpdateEvent>> GetEventsByAgentAsync(Guid agentId, int limit = 100, CancellationToken cancellationToken = default);
    Task<AgentUpdateRolloutDashboardDto> GetRolloutDashboardAsync(Guid? clientId = null, Guid? siteId = null, int limit = 200, CancellationToken cancellationToken = default);

    Task<AgentUpdateManifestDto> GetManifestAsync(Guid agentId, AgentUpdateManifestRequest request, CancellationToken cancellationToken = default);
    Task<AgentUpdateRedirectPayload?> GetPresignedDownloadUrlAsync(Guid agentId, AgentUpdateDownloadRequest request, CancellationToken cancellationToken = default);
    Task<AgentUpdateEvent> RecordEventAsync(Guid agentId, AgentUpdateReportRequest request, CancellationToken cancellationToken = default);
}
