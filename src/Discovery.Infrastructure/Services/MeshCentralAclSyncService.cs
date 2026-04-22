using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Discovery.Core.Configuration;

namespace Discovery.Infrastructure.Services;

public class MeshCentralAclSyncService : IMeshCentralAclSyncService
{
    private readonly MeshCentralOptions _options;
    private readonly IAgentRepository _agentRepository;
    private readonly IMeshCentralApiService _meshCentralApiService;
    private readonly ILogger<MeshCentralAclSyncService> _logger;

    public MeshCentralAclSyncService(
        IOptions<MeshCentralOptions> options,
        IAgentRepository agentRepository,
        IMeshCentralApiService meshCentralApiService,
        ILogger<MeshCentralAclSyncService> logger)
    {
        _options = options.Value;
        _agentRepository = agentRepository;
        _meshCentralApiService = meshCentralApiService;
        _logger = logger;
    }

    public async Task<MeshCentralDeviceAclBatchResult> SyncUserDeviceAccessAsync(
        string meshUserId,
        IReadOnlyCollection<MeshCentralSitePolicyResolution> sitePolicies,
        bool forceRevoke = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(meshUserId))
            throw new InvalidOperationException("MeshCentral user id is required for device ACL sync.");

        var desiredNodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var sitePolicy in sitePolicies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var agents = await _agentRepository.GetBySiteIdAsync(sitePolicy.SiteId);
            var deviceRights = sitePolicy.DeviceRights;

            if (deviceRights == 0)
            {
                _logger.LogInformation(
                    "MeshCentral ACL sync skipped site {SiteId} for user {MeshUserId} because effective device rights resolved to zero.",
                    sitePolicy.SiteId,
                    meshUserId);
                continue;
            }

            if (deviceRights != sitePolicy.MeshRights)
            {
                _logger.LogInformation(
                    "MeshCentral ACL sync sanitized rights for user {MeshUserId} on site {SiteId}. RawRights={RawRights}, DeviceRights={DeviceRights}",
                    meshUserId,
                    sitePolicy.SiteId,
                    sitePolicy.MeshRights,
                    deviceRights);
            }

            foreach (var agent in agents)
            {
                if (!TryNormalizeNodeId(agent, out var meshNodeId))
                    continue;

                if (desiredNodes.TryGetValue(meshNodeId, out var existingRights))
                {
                    desiredNodes[meshNodeId] = existingRights | deviceRights;
                    continue;
                }

                desiredNodes[meshNodeId] = deviceRights;
            }
        }

        var allKnownNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in await _agentRepository.GetAllAsync())
        {
            if (TryNormalizeNodeId(agent, out var meshNodeId))
                allKnownNodeIds.Add(meshNodeId);
        }

        var applied = 0;
        foreach (var target in desiredNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _meshCentralApiService.EnsureUserOnDeviceAsync(meshUserId, target.Key, target.Value, cancellationToken);
            applied++;
        }

        var revocationCandidates = allKnownNodeIds.Except(desiredNodes.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var revokeMissing = forceRevoke || _options.IdentitySyncDeviceAclRevocationEnabled;
        var revoked = 0;
        foreach (var nodeId in revocationCandidates)
        {
            if (!revokeMissing)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            await _meshCentralApiService.RemoveUserFromDeviceAsync(meshUserId, nodeId, cancellationToken);
            revoked++;
        }

        if (!revokeMissing && revocationCandidates.Length > 0)
        {
            _logger.LogInformation(
                "MeshCentral ACL revocation skipped for user {MeshUserId}. Candidates={Candidates}",
                meshUserId,
                revocationCandidates.Length);
        }

        return new MeshCentralDeviceAclBatchResult
        {
            DesiredNodeCount = desiredNodes.Count,
            DeviceBindingsApplied = applied,
            DeviceBindingsRevoked = revoked,
            DeviceBindingsRevocationCandidates = revocationCandidates.Length
        };
    }

    private bool TryNormalizeNodeId(Agent agent, out string meshNodeId)
    {
        meshNodeId = string.Empty;

        if (agent.ZeroTouchPending || string.IsNullOrWhiteSpace(agent.MeshCentralNodeId))
            return false;

        var normalized = agent.MeshCentralNodeId.Trim();
        if (!normalized.StartsWith("node/", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("MeshCentral node id inválido ignorado para agent {AgentId}: {MeshNodeId}", agent.Id, agent.MeshCentralNodeId);
            return false;
        }

        meshNodeId = normalized;
        return true;
    }
}