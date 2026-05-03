using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

public class MeshCentralAclSyncServiceTests
{
    [Test]
    public async Task SyncUserDeviceAccessAsync_ShouldGrantDesiredNodesAndRevokeOthers()
    {
        var siteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var agents = new[]
        {
            new Agent
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SiteId = siteId,
                Hostname = "agent-01",
                MeshCentralNodeId = "node//desired"
            },
            new Agent
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                SiteId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Hostname = "agent-02",
                MeshCentralNodeId = "node//revoke"
            },
            new Agent
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                SiteId = siteId,
                Hostname = "agent-03",
                MeshCentralNodeId = "invalid-node"
            },
            new Agent
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                SiteId = siteId,
                Hostname = "agent-04",
                MeshCentralNodeId = "node//pending",
                ZeroTouchPending = true
            }
        };

        var agentRepository = new InMemoryAgentRepository(agents);
        var meshCentralApiService = new RecordingMeshCentralApiService();
        var service = new MeshCentralAclSyncService(
            Options.Create(new Discovery.Core.Configuration.MeshCentralOptions
            {
                IdentitySyncDeviceAclRevocationEnabled = true
            }),
            agentRepository,
            meshCentralApiService,
            NullLogger<MeshCentralAclSyncService>.Instance);

        var result = await service.SyncUserDeviceAccessAsync(
            "user//mesh-user",
            new[]
            {
                new MeshCentralSitePolicyResolution
                {
                    SiteId = siteId,
                    MeshRights = 262152,
                    Sources = ["role:site"]
                }
            },
            cancellationToken: CancellationToken.None);

        Assert.That(result.DesiredNodeCount, Is.EqualTo(1));
        Assert.That(result.DeviceBindingsApplied, Is.EqualTo(1));
        Assert.That(result.DeviceBindingsRevoked, Is.EqualTo(1));
        Assert.That(result.DeviceBindingsRevocationCandidates, Is.EqualTo(1));

        Assert.That(meshCentralApiService.DeviceGrants, Has.Count.EqualTo(1));
        Assert.That(meshCentralApiService.DeviceGrants[0], Is.EqualTo(("user//mesh-user", "node//desired", 262152)));

        Assert.That(meshCentralApiService.DeviceRevocations, Has.Count.EqualTo(1));
        Assert.That(meshCentralApiService.DeviceRevocations[0], Is.EqualTo(("user//mesh-user", "node//revoke")));
    }

    [Test]
    public async Task SyncUserDeviceAccessAsync_ShouldSanitizeDisallowedBitsBeforeGranting()
    {
        var siteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var agentRepository = new InMemoryAgentRepository([
            new Agent
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SiteId = siteId,
                Hostname = "agent-01",
                MeshCentralNodeId = "node//desired"
            }
        ]);
        var meshCentralApiService = new RecordingMeshCentralApiService();
        var service = new MeshCentralAclSyncService(
            Options.Create(new Discovery.Core.Configuration.MeshCentralOptions()),
            agentRepository,
            meshCentralApiService,
            NullLogger<MeshCentralAclSyncService>.Instance);

        await service.SyncUserDeviceAccessAsync(
            "user//mesh-user",
            [
                new MeshCentralSitePolicyResolution
                {
                    SiteId = siteId,
                    MeshRights = 1 | 2 | 4 | 8 | 16,
                    Sources = ["role:admin"]
                }
            ],
            cancellationToken: CancellationToken.None);

        Assert.That(meshCentralApiService.DeviceGrants, Has.Count.EqualTo(1));
        Assert.That(meshCentralApiService.DeviceGrants[0], Is.EqualTo(("user//mesh-user", "node//desired", 24)));
    }

    [Test]
    public async Task SyncUserDeviceAccessAsync_ShouldKeepRevocationAsCandidate_WhenDisabled()
    {
        var siteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var agentRepository = new InMemoryAgentRepository([
            new Agent
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SiteId = siteId,
                Hostname = "agent-01",
                MeshCentralNodeId = "node//desired"
            },
            new Agent
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                SiteId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Hostname = "agent-02",
                MeshCentralNodeId = "node//candidate"
            }
        ]);
        var meshCentralApiService = new RecordingMeshCentralApiService();
        var service = new MeshCentralAclSyncService(
            Options.Create(new Discovery.Core.Configuration.MeshCentralOptions
            {
                IdentitySyncDeviceAclRevocationEnabled = false
            }),
            agentRepository,
            meshCentralApiService,
            NullLogger<MeshCentralAclSyncService>.Instance);

        var result = await service.SyncUserDeviceAccessAsync(
            "user//mesh-user",
            [
                new MeshCentralSitePolicyResolution
                {
                    SiteId = siteId,
                    MeshRights = 8,
                    Sources = ["role:operator"]
                }
            ],
            cancellationToken: CancellationToken.None);

        Assert.That(result.DeviceBindingsRevoked, Is.EqualTo(0));
        Assert.That(result.DeviceBindingsRevocationCandidates, Is.EqualTo(1));
        Assert.That(meshCentralApiService.DeviceRevocations, Is.Empty);
    }

    private sealed class InMemoryAgentRepository : IAgentRepository
    {
        private readonly List<Agent> _agents;

        public InMemoryAgentRepository(IEnumerable<Agent> agents)
        {
            _agents = agents.ToList();
        }

        public Task<Agent?> GetByIdAsync(Guid id)
            => Task.FromResult(_agents.SingleOrDefault(agent => agent.Id == id));

        public Task<IEnumerable<Agent>> GetAllAsync()
            => Task.FromResult<IEnumerable<Agent>>(_agents);

        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId)
            => Task.FromResult<IEnumerable<Agent>>(_agents.Where(agent => agent.SiteId == siteId).ToArray());

        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId)
            => throw new NotImplementedException();

        public Task<Agent> CreateAsync(Agent agent)
            => throw new NotImplementedException();

        public Task UpdateAsync(Agent agent)
            => throw new NotImplementedException();

        public Task UpdateStatusAsync(Guid id, Discovery.Core.Enums.AgentStatus status, string? ipAddress)
            => throw new NotImplementedException();

        public Task ApproveZeroTouchAsync(Guid agentId)
            => throw new NotImplementedException();

        public Task DeleteAsync(Guid id)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class RecordingMeshCentralApiService : IMeshCentralApiService
    {
        public List<(string UserId, string NodeId, int Rights)> DeviceGrants { get; } = [];
        public List<(string UserId, string NodeId)> DeviceRevocations { get; } = [];

        public Task<MeshCentralInstallInstructions> ProvisionInstallAsync(Client client, Site site, string discoveryDeployToken, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralUserUpsertResult> EnsureUserAsync(Discovery.Core.Entities.Identity.User user, string preferredUsername, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralMembershipSyncResult> EnsureUserInMeshAsync(string meshUserId, string meshId, int meshAdminRights = 0, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralMembershipSyncResult> RemoveUserFromMeshAsync(string meshUserId, string meshId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralDeviceAclSyncResult> EnsureUserOnDeviceAsync(string meshUserId, string meshNodeId, int rights, CancellationToken cancellationToken = default)
        {
            DeviceGrants.Add((meshUserId, meshNodeId, rights));
            return Task.FromResult(new MeshCentralDeviceAclSyncResult
            {
                UserId = meshUserId,
                NodeId = meshNodeId,
                Granted = true,
                AppliedRights = rights
            });
        }

        public Task<MeshCentralDeviceAclSyncResult> RemoveUserFromDeviceAsync(string meshUserId, string meshNodeId, CancellationToken cancellationToken = default)
        {
            DeviceRevocations.Add((meshUserId, meshNodeId));
            return Task.FromResult(new MeshCentralDeviceAclSyncResult
            {
                UserId = meshUserId,
                NodeId = meshNodeId,
                Removed = true
            });
        }

        public Task<IReadOnlyCollection<MeshCentralNodeRef>> ListNodesAsync(string? meshId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task DeleteUserAsync(string meshUserId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task RemoveDeviceAsync(string meshNodeId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralHealthCheckResult> RunHealthCheckAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralGroupBindingSyncResult> EnsureSiteGroupBindingAsync(Client client, Site site, string desiredGroupPolicyProfile, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}