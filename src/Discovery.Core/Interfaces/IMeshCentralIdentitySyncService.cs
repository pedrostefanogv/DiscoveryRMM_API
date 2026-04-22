namespace Discovery.Core.Interfaces;

/// <summary>
/// Contrato de preparacao para fase 3: sincronizacao de identidade de usuarios com MeshCentral.
/// </summary>
public interface IMeshCentralIdentitySyncService
{
    Task<MeshCentralIdentitySyncPreview> BuildPreviewAsync(Guid clientId, Guid siteId, string localUsername, CancellationToken cancellationToken = default);

    Task<MeshCentralIdentitySyncResult> SyncUserOnCreateAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<MeshCentralIdentitySyncResult> SyncUserOnUpdatedAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<MeshCentralIdentitySyncResult> SyncUserScopesAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<MeshCentralIdentitySyncResult> DeprovisionUserAsync(Guid userId, bool deleteRemoteUser = false, CancellationToken cancellationToken = default);

    Task<MeshCentralIdentityBackfillReport> RunBackfillAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        bool applyChanges = false,
        CancellationToken cancellationToken = default);
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

public sealed class MeshCentralIdentitySyncResult
{
    public required Guid UserId { get; init; }
    public required string LocalLogin { get; init; }
    public required bool Synced { get; init; }
    public required string MeshUsername { get; init; }
    public string? MeshUserId { get; init; }
    public int SiteBindingsApplied { get; init; }
    public int RightsUpdatesApplied { get; init; }
    public int DeviceBindingsApplied { get; init; }
    public int DeviceBindingsRevoked { get; init; }
    public int DeviceBindingsRevocationCandidates { get; init; }
    public string? Error { get; init; }
}

public sealed class MeshCentralIdentityBackfillReport
{
    public required bool ApplyChanges { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public required DateTime FinishedAtUtc { get; init; }
    public required int TotalUsers { get; init; }
    public required int SyncedUsers { get; init; }
    public required int FailedUsers { get; init; }
    public required IReadOnlyCollection<MeshCentralIdentityBackfillItem> Items { get; init; }
}

public sealed class MeshCentralIdentityBackfillItem
{
    public required Guid UserId { get; init; }
    public required string Login { get; init; }
    public required string MeshUsername { get; init; }
    public required bool Applied { get; init; }
    public required bool Success { get; init; }
    public int SiteBindingsApplied { get; init; }
    public int RightsUpdatesApplied { get; init; }
    public int DeviceBindingsApplied { get; init; }
    public int DeviceBindingsRevoked { get; init; }
    public int DeviceBindingsRevocationCandidates { get; init; }
    public string? Error { get; init; }
}
