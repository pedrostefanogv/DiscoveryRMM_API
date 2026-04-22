using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;

namespace Discovery.Core.Interfaces;

public interface IMeshCentralApiService
{
    Task<MeshCentralInstallInstructions> ProvisionInstallAsync(
        Client client,
        Site site,
        string discoveryDeployToken,
        CancellationToken cancellationToken = default);

    Task<MeshCentralUserUpsertResult> EnsureUserAsync(
        User user,
        string preferredUsername,
        CancellationToken cancellationToken = default);

    Task<MeshCentralMembershipSyncResult> EnsureUserInMeshAsync(
        string meshUserId,
        string meshId,
        int meshAdminRights = 0,
        CancellationToken cancellationToken = default);

    Task<MeshCentralMembershipSyncResult> RemoveUserFromMeshAsync(
        string meshUserId,
        string meshId,
        CancellationToken cancellationToken = default);

    Task<MeshCentralDeviceAclSyncResult> EnsureUserOnDeviceAsync(
        string meshUserId,
        string meshNodeId,
        int rights,
        CancellationToken cancellationToken = default);

    Task<MeshCentralDeviceAclSyncResult> RemoveUserFromDeviceAsync(
        string meshUserId,
        string meshNodeId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<MeshCentralNodeRef>> ListNodesAsync(
        string? meshId = null,
        CancellationToken cancellationToken = default);

    Task DeleteUserAsync(
        string meshUserId,
        CancellationToken cancellationToken = default);

    Task RemoveDeviceAsync(
        string meshNodeId,
        CancellationToken cancellationToken = default);

    Task<MeshCentralHealthCheckResult> RunHealthCheckAsync(
        CancellationToken cancellationToken = default);

    Task<MeshCentralGroupBindingSyncResult> EnsureSiteGroupBindingAsync(
        Client client,
        Site site,
        string desiredGroupPolicyProfile,
        CancellationToken cancellationToken = default);
}

public sealed class MeshCentralUserUpsertResult
{
    public required string UserId { get; init; }
    public required string Username { get; init; }
    public required bool Created { get; init; }
}

public sealed class MeshCentralMembershipSyncResult
{
    public required string UserId { get; init; }
    public required string MeshId { get; init; }
    public required bool Added { get; init; }
    public bool RightsUpdated { get; init; }
    public int? PreviousRights { get; init; }
    public int? AppliedRights { get; init; }
}

public sealed class MeshCentralGroupBindingSyncResult
{
    public required Guid SiteId { get; init; }
    public required Guid ClientId { get; init; }
    public required string GroupName { get; init; }
    public required string MeshId { get; init; }
    public required string AppliedProfile { get; init; }
    public string? PreviousGroupName { get; init; }
    public string? PreviousMeshId { get; init; }
    public string? PreviousAppliedProfile { get; init; }
    public bool GroupBindingChanged { get; init; }
    public bool ProfileChanged { get; init; }
}

public sealed class MeshCentralDeviceAclSyncResult
{
    public required string UserId { get; init; }
    public required string NodeId { get; init; }
    public bool Granted { get; init; }
    public bool Removed { get; init; }
    public int? AppliedRights { get; init; }
}

public sealed class MeshCentralNodeRef
{
    public required string NodeId { get; init; }
    public required string MeshId { get; init; }
    public string? Name { get; init; }
    public string? Hostname { get; init; }
}

public sealed class MeshCentralHealthCheckResult
{
    public required bool ControlSocketConnected { get; init; }
    public required string PublicBaseUrl { get; init; }
    public required string AdministrativeBaseUrl { get; init; }
    public required string TechnicalUsername { get; init; }
    public int MeshCount { get; init; }
    public int UserCount { get; init; }
}
