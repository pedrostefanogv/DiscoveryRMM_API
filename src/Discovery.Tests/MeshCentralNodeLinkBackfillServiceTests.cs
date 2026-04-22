using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

public class MeshCentralNodeLinkBackfillServiceTests
{
    [Test]
    public async Task RunBackfillAsync_ShouldSuggestUniqueHostnameMatch_WithoutPersistingInDryRun()
    {
        var siteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var clientId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var agentRepository = new InMemoryAgentRepository([
            new Agent
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SiteId = siteId,
                Hostname = "agent-01"
            }
        ]);
        var siteConfigurationRepository = new InMemorySiteConfigurationRepository([
            new SiteConfiguration
            {
                SiteId = siteId,
                ClientId = clientId,
                MeshCentralMeshId = "mesh//site-a"
            }
        ]);
        var meshCentralApiService = new RecordingMeshCentralApiService([
            new MeshCentralNodeRef
            {
                NodeId = "node//mesh-a-agent-01",
                MeshId = "mesh//site-a",
                Hostname = "agent-01",
                Name = "agent-01"
            }
        ]);
        var service = new MeshCentralNodeLinkBackfillService(
            agentRepository,
            siteConfigurationRepository,
            meshCentralApiService,
            NullLogger<MeshCentralNodeLinkBackfillService>.Instance);

        var report = await service.RunBackfillAsync(siteId: siteId, applyChanges: false, cancellationToken: CancellationToken.None);

        Assert.That(report.TotalAgents, Is.EqualTo(1));
        Assert.That(report.UpdatedAgents, Is.EqualTo(0));
        Assert.That(report.MissingAgents, Is.EqualTo(1));
        Assert.That(report.Items.Single().Status, Is.EqualTo("suggested"));
        Assert.That(report.Items.Single().SuggestedNodeId, Is.EqualTo("node//mesh-a-agent-01"));
        Assert.That((await agentRepository.GetBySiteIdAsync(siteId)).Single().MeshCentralNodeId, Is.Null);
    }

    [Test]
    public async Task RunBackfillAsync_ShouldPersistUniqueHostnameMatch_WhenApplyChangesIsTrue()
    {
        var siteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var clientId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var agentRepository = new InMemoryAgentRepository([
            new Agent
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SiteId = siteId,
                Hostname = "agent-01"
            }
        ]);
        var service = new MeshCentralNodeLinkBackfillService(
            agentRepository,
            new InMemorySiteConfigurationRepository([
                new SiteConfiguration
                {
                    SiteId = siteId,
                    ClientId = clientId,
                    MeshCentralMeshId = "mesh//site-a"
                }
            ]),
            new RecordingMeshCentralApiService([
                new MeshCentralNodeRef
                {
                    NodeId = "node//mesh-a-agent-01",
                    MeshId = "mesh//site-a",
                    Hostname = "agent-01",
                    Name = "agent-01"
                }
            ]),
            NullLogger<MeshCentralNodeLinkBackfillService>.Instance);

        var report = await service.RunBackfillAsync(siteId: siteId, applyChanges: true, cancellationToken: CancellationToken.None);

        Assert.That(report.UpdatedAgents, Is.EqualTo(1));
        Assert.That(report.Items.Single().Status, Is.EqualTo("linked"));
        Assert.That(report.Items.Single().Applied, Is.True);
        Assert.That((await agentRepository.GetBySiteIdAsync(siteId)).Single().MeshCentralNodeId, Is.EqualTo("node//mesh-a-agent-01"));
    }

    [Test]
    public async Task RunBackfillAsync_ShouldMarkAmbiguous_WhenMultipleMatchesExist()
    {
        var siteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var clientId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var service = new MeshCentralNodeLinkBackfillService(
            new InMemoryAgentRepository([
                new Agent
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    SiteId = siteId,
                    Hostname = "agent-01"
                }
            ]),
            new InMemorySiteConfigurationRepository([
                new SiteConfiguration
                {
                    SiteId = siteId,
                    ClientId = clientId,
                    MeshCentralMeshId = "mesh//site-a"
                }
            ]),
            new RecordingMeshCentralApiService([
                new MeshCentralNodeRef
                {
                    NodeId = "node//mesh-a-agent-01-a",
                    MeshId = "mesh//site-a",
                    Hostname = "agent-01"
                },
                new MeshCentralNodeRef
                {
                    NodeId = "node//mesh-a-agent-01-b",
                    MeshId = "mesh//site-a",
                    Name = "agent-01"
                }
            ]),
            NullLogger<MeshCentralNodeLinkBackfillService>.Instance);

        var report = await service.RunBackfillAsync(siteId: siteId, applyChanges: false, cancellationToken: CancellationToken.None);

        Assert.That(report.AmbiguousAgents, Is.EqualTo(1));
        Assert.That(report.Items.Single().Status, Is.EqualTo("ambiguous"));
        Assert.That(report.Items.Single().CandidateNodeIds, Has.Count.EqualTo(2));
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
            => Task.FromResult<IEnumerable<Agent>>(_agents);

        public Task<Agent> CreateAsync(Agent agent)
            => throw new NotImplementedException();

        public Task UpdateAsync(Agent agent)
        {
            var existing = _agents.Single(item => item.Id == agent.Id);
            existing.MeshCentralNodeId = agent.MeshCentralNodeId;
            return Task.CompletedTask;
        }

        public Task UpdateStatusAsync(Guid id, Discovery.Core.Enums.AgentStatus status, string? ipAddress)
            => throw new NotImplementedException();

        public Task ApproveZeroTouchAsync(Guid agentId)
            => throw new NotImplementedException();

        public Task DeleteAsync(Guid id)
            => throw new NotImplementedException();
    }

    private sealed class InMemorySiteConfigurationRepository : ISiteConfigurationRepository
    {
        private readonly Dictionary<Guid, SiteConfiguration> _items;

        public InMemorySiteConfigurationRepository(IEnumerable<SiteConfiguration> items)
        {
            _items = items.ToDictionary(item => item.SiteId);
        }

        public Task<SiteConfiguration?> GetBySiteIdAsync(Guid siteId)
            => Task.FromResult(_items.TryGetValue(siteId, out var value) ? value : null);

        public Task<IEnumerable<SiteConfiguration>> GetByClientIdAsync(Guid clientId)
            => Task.FromResult<IEnumerable<SiteConfiguration>>(_items.Values.Where(item => item.ClientId == clientId).ToArray());

        public Task CreateAsync(SiteConfiguration config)
            => throw new NotImplementedException();

        public Task UpdateAsync(SiteConfiguration config)
            => throw new NotImplementedException();

        public Task DeleteAsync(Guid siteId)
            => throw new NotImplementedException();
    }

    private sealed class RecordingMeshCentralApiService : IMeshCentralApiService
    {
        private readonly IReadOnlyCollection<MeshCentralNodeRef> _nodes;

        public RecordingMeshCentralApiService(IReadOnlyCollection<MeshCentralNodeRef> nodes)
        {
            _nodes = nodes;
        }

        public Task<MeshCentralInstallInstructions> ProvisionInstallAsync(Client client, Site site, string discoveryDeployToken, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralUserUpsertResult> EnsureUserAsync(Discovery.Core.Entities.Identity.User user, string preferredUsername, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralMembershipSyncResult> EnsureUserInMeshAsync(string meshUserId, string meshId, int meshAdminRights = 0, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralMembershipSyncResult> RemoveUserFromMeshAsync(string meshUserId, string meshId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralDeviceAclSyncResult> EnsureUserOnDeviceAsync(string meshUserId, string meshNodeId, int rights, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MeshCentralDeviceAclSyncResult> RemoveUserFromDeviceAsync(string meshUserId, string meshNodeId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyCollection<MeshCentralNodeRef>> ListNodesAsync(string? meshId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<MeshCentralNodeRef>>(
                string.IsNullOrWhiteSpace(meshId)
                    ? _nodes
                    : _nodes.Where(node => string.Equals(node.MeshId, meshId, StringComparison.OrdinalIgnoreCase)).ToArray());

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