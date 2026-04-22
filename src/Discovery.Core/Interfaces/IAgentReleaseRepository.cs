using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAgentReleaseRepository
{
    Task<IReadOnlyList<AgentRelease>> ListAsync(bool includeInactive = false, string? channel = null, CancellationToken cancellationToken = default);
    Task<AgentRelease?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AgentRelease?> GetByVersionAsync(string version, string channel, CancellationToken cancellationToken = default);
    Task<AgentRelease> CreateAsync(AgentRelease release, CancellationToken cancellationToken = default);
    Task UpdateAsync(AgentRelease release, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AgentReleaseArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken cancellationToken = default);
    Task<AgentReleaseArtifact?> GetArtifactAsync(
        Guid releaseId,
        string platform,
        string architecture,
        AgentReleaseArtifactType artifactType,
        CancellationToken cancellationToken = default);
    Task<AgentReleaseArtifact> CreateArtifactAsync(AgentReleaseArtifact artifact, CancellationToken cancellationToken = default);
    Task UpdateArtifactAsync(AgentReleaseArtifact artifact, CancellationToken cancellationToken = default);
    Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);
}
