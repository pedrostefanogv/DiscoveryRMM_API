namespace Meduza.Core.Interfaces;

/// <summary>
/// Contrato de preparacao para fase 3: sincronizacao de identidade de usuarios com MeshCentral.
/// </summary>
public interface IMeshCentralIdentitySyncService
{
    Task<MeshCentralIdentitySyncPreview> BuildPreviewAsync(Guid clientId, Guid siteId, string localUsername, CancellationToken cancellationToken = default);
}

public sealed class MeshCentralIdentitySyncPreview
{
    public required Guid ClientId { get; init; }
    public required Guid SiteId { get; init; }
    public required string LocalUsername { get; init; }
    public required string SuggestedMeshUsername { get; init; }
    public required string SuggestedGroupName { get; init; }
    public required bool RequiresCreateUser { get; init; }
    public required bool RequiresGroupBinding { get; init; }
}
