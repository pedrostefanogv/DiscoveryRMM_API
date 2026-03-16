using Meduza.Core.Entities;
using Meduza.Core.Entities.Identity;

namespace Meduza.Core.Interfaces;

public interface IMeshCentralApiService
{
    Task<MeshCentralInstallInstructions> ProvisionInstallAsync(
        Client client,
        Site site,
        string meduzaDeployToken,
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

    Task DeleteUserAsync(
        string meshUserId,
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
