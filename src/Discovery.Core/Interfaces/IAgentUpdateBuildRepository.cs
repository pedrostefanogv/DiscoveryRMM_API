using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAgentUpdateBuildRepository
{
    Task<AgentUpdateBuild?> GetCurrentAsync(
        string platform,
        string architecture,
        AgentReleaseArtifactType artifactType,
        CancellationToken cancellationToken = default);

    Task<AgentUpdateBuild> CreateAsync(AgentUpdateBuild build, CancellationToken cancellationToken = default);

    Task DeactivateCurrentAsync(
        string platform,
        string architecture,
        AgentReleaseArtifactType artifactType,
        Guid keepActiveBuildId,
        CancellationToken cancellationToken = default);
}
