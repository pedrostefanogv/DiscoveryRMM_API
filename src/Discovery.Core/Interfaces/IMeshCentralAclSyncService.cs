namespace Discovery.Core.Interfaces;

public interface IMeshCentralAclSyncService
{
    Task<MeshCentralDeviceAclBatchResult> SyncUserDeviceAccessAsync(
        string meshUserId,
        IReadOnlyCollection<MeshCentralSitePolicyResolution> sitePolicies,
        bool forceRevoke = false,
        CancellationToken cancellationToken = default);
}

public sealed class MeshCentralDeviceAclBatchResult
{
    public int DesiredNodeCount { get; init; }
    public int DeviceBindingsApplied { get; init; }
    public int DeviceBindingsRevoked { get; init; }
    public int DeviceBindingsRevocationCandidates { get; init; }
}