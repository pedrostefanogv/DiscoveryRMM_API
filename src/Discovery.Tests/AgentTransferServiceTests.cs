using Discovery.Api.Services;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Testes da notificação pós-transferência (Fase 1/2 do plano
/// AGENT_TRANSFER_SYNC_FIX_PLAN): dual-publish do sync ping (subjects antigo e novo),
/// persistência do delivery e comando nats.reconnect no subject antigo.
/// </summary>
public class AgentTransferServiceTests
{
    private static readonly Guid ClientAId = Guid.NewGuid();
    private static readonly Guid ClientBId = Guid.NewGuid();
    private static readonly Guid SiteAId = Guid.NewGuid();
    private static readonly Guid SiteBId = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private FakeAgentTransferRepo _agentRepo = null!;
    private FakeTransferSiteRepo _siteRepo = null!;
    private FakeTransferClientRepo _clientRepo = null!;
    private FakePermissionService _permission = null!;
    private FakeDeliveryRepo _deliveryRepo = null!;
    private FakeTransferMessaging _messaging = null!;

    [SetUp]
    public void SetUp()
    {
        _agentRepo = new FakeAgentTransferRepo();
        _siteRepo = new FakeTransferSiteRepo();
        _clientRepo = new FakeTransferClientRepo();
        _permission = new FakePermissionService();
        _deliveryRepo = new FakeDeliveryRepo();
        _messaging = new FakeTransferMessaging();
    }

    private AgentTransferService CreateService() => new(
        _agentRepo,
        _siteRepo,
        _clientRepo,
        _permission,
        _messaging,
        new FakeRedisService(),
        _deliveryRepo,
        NullLogger<AgentTransferService>.Instance);

    private void SetupSuccessfulScenario()
    {
        _siteRepo.Sites[SiteAId] = new Site { Id = SiteAId, ClientId = ClientAId, Name = "Site A", IsActive = true };
        _siteRepo.Sites[SiteBId] = new Site { Id = SiteBId, ClientId = ClientBId, Name = "Site B", IsActive = true };
        _clientRepo.Clients[ClientAId] = new Client { Id = ClientAId, Name = "Client A" };
        _clientRepo.Clients[ClientBId] = new Client { Id = ClientBId, Name = "Client B" };
        _agentRepo.Agent = new Agent { Id = AgentId, SiteId = SiteAId, Hostname = "host-1" };
        _agentRepo.OnTransfer = (agentId, newSiteId) =>
        {
            if (agentId == AgentId && _agentRepo.Agent is not null)
                _agentRepo.Agent.SiteId = newSiteId;
        };
    }

    [Test]
    public async Task Transfer_PublishesSyncPingOnBothSubjects()
    {
        SetupSuccessfulScenario();
        var svc = CreateService();

        await svc.TransferAsync(AgentId, SiteBId, UserId, null, CancellationToken.None);

        Assert.That(_messaging.SyncPingSubjects, Has.Count.EqualTo(2));
        Assert.That(_messaging.SyncPingSubjects,
            Does.Contain($"tenant.{ClientAId}.site.{SiteAId}.agent.{AgentId}.sync.ping"));
        Assert.That(_messaging.SyncPingSubjects,
            Does.Contain($"tenant.{ClientBId}.site.{SiteBId}.agent.{AgentId}.sync.ping"));
    }

    [Test]
    public async Task Transfer_SendsReconnectCommandOnOldSubject()
    {
        SetupSuccessfulScenario();
        var svc = CreateService();

        await svc.TransferAsync(AgentId, SiteBId, UserId, null, CancellationToken.None);

        Assert.That(_messaging.CommandSubjects, Has.Count.EqualTo(1));
        Assert.That(_messaging.CommandSubjects[0],
            Is.EqualTo($"tenant.{ClientAId}.site.{SiteAId}.agent.{AgentId}.command"));
        Assert.That(_messaging.CommandTypes[0], Is.EqualTo("nats.reconnect"));
        Assert.That(_messaging.CommandPayloads[0], Does.Contain("agent-transferred"));
        Assert.That(_messaging.CommandPayloads[0], Does.Contain(SiteBId.ToString()));
    }

    [Test]
    public async Task Transfer_PersistsSyncPingDelivery()
    {
        SetupSuccessfulScenario();
        var svc = CreateService();

        await svc.TransferAsync(AgentId, SiteBId, UserId, null, CancellationToken.None);

        Assert.That(_deliveryRepo.SentDeliveries, Has.Count.EqualTo(1));
        Assert.That(_deliveryRepo.SentDeliveries[0].AgentId, Is.EqualTo(AgentId));
        Assert.That(_deliveryRepo.SentDeliveries[0].Resource, Is.EqualTo(SyncResourceType.Configuration));
    }

    [Test]
    public async Task Transfer_ResultIndicatesAgentNotified()
    {
        SetupSuccessfulScenario();
        var svc = CreateService();

        var result = await svc.TransferAsync(AgentId, SiteBId, UserId, null, CancellationToken.None);

        Assert.That(result.AgentNotified, Is.True);
        Assert.That(result.Agent.SiteId, Is.EqualTo(SiteBId));
    }

    [Test]
    public async Task Transfer_WhenMessagingFails_StillSucceedsAndReportsNotNotified()
    {
        SetupSuccessfulScenario();
        _messaging.ThrowOnPublish = true;
        var svc = CreateService();

        var result = await svc.TransferAsync(AgentId, SiteBId, UserId, null, CancellationToken.None);

        Assert.That(result.Agent.SiteId, Is.EqualTo(SiteBId));
        Assert.That(result.AgentNotified, Is.False);
    }

    // ── Fakes ──

    private sealed class FakeAgentTransferRepo : IAgentRepository
    {
        public Agent? Agent { get; set; }
        public Action<Guid, Guid>? OnTransfer { get; set; }

        public Task<Agent?> GetByIdAsync(Guid id)
            => Task.FromResult(Agent is not null && Agent.Id == id ? Agent : null);
        public Task<IEnumerable<Agent>> GetAllAsync()
            => Task.FromResult<IEnumerable<Agent>>(Agent is null ? [] : [Agent]);
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => Task.FromResult<IEnumerable<Agent>>([]);
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => Task.FromResult<IEnumerable<Agent>>([]);
        public Task<Agent> CreateAsync(Agent agent) => Task.FromResult(agent);
        public Task UpdateAsync(Agent agent) => Task.CompletedTask;
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);
        public Task ApproveZeroTouchAsync(Guid agentId) => Task.CompletedTask;
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => Task.CompletedTask;
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId)
        {
            OnTransfer?.Invoke(agentId, newSiteId);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);
    }

    private sealed class FakeTransferSiteRepo : ISiteRepository
    {
        public Dictionary<Guid, Site> Sites { get; } = [];
        public Task<Site?> GetByIdAsync(Guid id) => Task.FromResult(Sites.GetValueOrDefault(id));
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>([]);
        public Task<Site> CreateAsync(Site site) => Task.FromResult(site);
        public Task UpdateAsync(Site site) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    private sealed class FakeTransferClientRepo : IClientRepository
    {
        public Dictionary<Guid, Client> Clients { get; } = [];
        public Task<Client?> GetByIdAsync(Guid id) => Task.FromResult(Clients.GetValueOrDefault(id));
        public Task<IEnumerable<Client>> GetAllAsync(bool includeInactive = false)
            => Task.FromResult<IEnumerable<Client>>([]);
        public Task<Client> CreateAsync(Client client) => Task.FromResult(client);
        public Task UpdateAsync(Client client) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    private sealed class FakePermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, ResourceType resource, ActionType action, ScopeLevel scopeLevel = ScopeLevel.Global, Guid? scopeId = null, Guid? parentScopeId = null)
            => Task.FromResult(true);
        public Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess());
        public Task InvalidateUserCacheAsync(Guid userId) => Task.CompletedTask;
        public Task InvalidateAllCacheAsync() => Task.CompletedTask;
    }

    private sealed class FakeDeliveryRepo : ISyncPingDeliveryRepository
    {
        public List<(Guid EventId, Guid AgentId, SyncResourceType Resource, string Revision)> SentDeliveries { get; } = [];

        public Task<SyncPingDelivery> CreateSentAsync(Guid eventId, Guid agentId, SyncResourceType resource, string revision)
        {
            SentDeliveries.Add((eventId, agentId, resource, revision));
            return Task.FromResult(new SyncPingDelivery { EventId = eventId, AgentId = agentId, Resource = resource, Revision = revision });
        }

        public Task<SyncPingDelivery> UpsertAckAsync(Guid eventId, Guid agentId, SyncPingAckRequest request, DateTime acknowledgedAt)
            => throw new NotSupportedException();

        public Task<bool> IsAcknowledgedAsync(Guid eventId, Guid agentId, string revision)
            => Task.FromResult(false);
    }

    private sealed class FakeRedisService : IRedisService
    {
        public bool IsConnected => false;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.CompletedTask;
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(false);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(0);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }

    private sealed class FakeTransferMessaging : IAgentMessaging
    {
        public bool IsConnected => true;
        public bool ThrowOnPublish { get; set; }
        public List<string> SyncPingSubjects { get; } = [];
        public List<string> CommandSubjects { get; } = [];
        public List<string> CommandTypes { get; } = [];
        public List<string> CommandPayloads { get; } = [];

        public Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload)
            => Task.CompletedTask;

        public Task SendCommandToSubjectAsync(Guid clientId, Guid siteId, Guid agentId, Guid commandId, string commandType, string payload)
        {
            if (ThrowOnPublish) throw new InvalidOperationException("NATS unavailable.");
            CommandSubjects.Add($"tenant.{clientId}.site.{siteId}.agent.{agentId}.command");
            CommandTypes.Add(commandType);
            CommandPayloads.Add(payload);
            return Task.CompletedTask;
        }

        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, Guid overrideClientId, Guid overrideSiteId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnPublish) throw new InvalidOperationException("NATS unavailable.");
            SyncPingSubjects.Add($"tenant.{overrideClientId}.site.{overrideSiteId}.agent.{agentId}.sync.ping");
            return Task.CompletedTask;
        }

        public Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
