namespace Discovery.Core.Interfaces;

/// <summary>
/// Resolve direitos de MeshCentral por site com base na ACL da aplicacao.
/// </summary>
public interface IMeshCentralPolicyResolver
{
    Task<MeshCentralUserPolicyResolution> ResolveForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed class MeshCentralUserPolicyResolution
{
    public required Guid UserId { get; init; }
    public required IReadOnlyCollection<MeshCentralSitePolicyResolution> Sites { get; init; }
}

public sealed class MeshCentralSitePolicyResolution
{
    public required Guid SiteId { get; init; }
    public required int MeshRights { get; init; }
    public required IReadOnlyCollection<string> Sources { get; init; }
}
