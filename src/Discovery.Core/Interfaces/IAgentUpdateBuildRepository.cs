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

    /// <summary>M builds inativos (IsActive = false) — para cleanup de disco.</summary>
    Task<IReadOnlyList<AgentUpdateBuild>> ListInactiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Remove um build do DB (o caller é responsável por deletar o arquivo físico).</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
