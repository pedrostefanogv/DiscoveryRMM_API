using Discovery.Core.Cqrs.Sites.PowerManagement.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Sites.CommandHandlers;

namespace Discovery.Tests;

public class SitePowerCommandHandlersTests
{
    private static Guid _siteId = Guid.NewGuid();
    private static Guid _clientId = Guid.NewGuid();

    private sealed class FakeSiteRepository : ISiteRepository
    {
        private readonly Site _site;
        public FakeSiteRepository(Site site) => _site = site;
        public Task<Site?> GetByIdAsync(Guid id) =>
            Task.FromResult(id == _site.Id ? (Site?)_site : null);
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => Task.FromResult<IEnumerable<Site>>([_site]);
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => Task.FromResult<IEnumerable<Site>>([_site]);
        public Task<Site> CreateAsync(Site s) => Task.FromResult(s);
        public Task UpdateAsync(Site s) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        private readonly List<Agent> _agents;
        public FakeAgentRepository(IEnumerable<Agent> agents) => _agents = agents.ToList();
        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult(_agents.FirstOrDefault(a => a.Id == id));
        public Task<IEnumerable<Agent>> GetAllAsync() => Task.FromResult<IEnumerable<Agent>>(_agents);
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => Task.FromResult<IEnumerable<Agent>>(_agents.Where(a => a.SiteId == siteId));
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => Task.FromResult<IEnumerable<Agent>>(_agents);
        public Task<Agent> CreateAsync(Agent agent) { _agents.Add(agent); return Task.FromResult(agent); }
        public Task UpdateAsync(Agent agent) => Task.CompletedTask;
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>(_agents.Where(a => a.Status == AgentStatus.Online).ToList());
        public Task ApproveZeroTouchAsync(Guid agentId) => Task.CompletedTask;
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => Task.CompletedTask;
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([]);
    }

    private sealed class FakeHardwareRepository : IAgentHardwareRepository
    {
        private readonly Dictionary<Guid, AgentHardwareComponents> _components;
        public FakeHardwareRepository(Dictionary<Guid, AgentHardwareComponents> components) => _components = components;
        public Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId) => Task.FromResult<AgentHardwareInfo?>(null);
        public Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId) =>
            Task.FromResult(_components.TryGetValue(agentId, out var c) ? c : new AgentHardwareComponents());
        public Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null) => Task.CompletedTask;
    }

    private sealed class FakeAgentMessaging : IAgentMessaging
    {
        public bool IsConnected { get; set; } = true;
        public int SiteFanoutCalls { get; private set; }
        public List<string> PublishedCommandTypes { get; } = [];
        public List<string> Payloads { get; } = [];

        public Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload) => Task.CompletedTask;
        public Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken ct = default)
        {
            SiteFanoutCalls++;
            PublishedCommandTypes.Add(envelope.CommandType);
            Payloads.Add(envelope.Payload);
            return Task.CompletedTask;
        }
        public Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, Guid overrideClientId, Guid overrideSiteId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCommandToSubjectAsync(Guid clientId, Guid siteId, Guid agentId, Guid commandId, string commandType, string payload) => Task.CompletedTask;
        public Task SubscribeToAgentMessagesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static Site NewSite() => new() { Id = _siteId, ClientId = _clientId, Name = "SiteGeral" };

    private static Agent NewAgent(Guid id, AgentStatus status, string? mac = null) =>
        new()
        {
            Id = id,
            SiteId = _siteId,
            Hostname = $"host-{Guid.NewGuid():N}",
            Status = status,
            MacAddress = mac,
        };

    // ── Restart ──

    [Test]
    public async Task Restart_WithOnlineAgents_PublishesFanoutWithRestart()
    {
        var online = NewAgent(Guid.NewGuid(), AgentStatus.Online);
        var offline = NewAgent(Guid.NewGuid(), AgentStatus.Offline);
        var msg = new FakeAgentMessaging();
        var handler = new SiteRestartCommandHandler(msg, new FakeSiteRepository(NewSite()), new FakeAgentRepository([online, offline]));

        var result = await handler.Handle(new SiteRestartCommand(_siteId, 15, false, "msg"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(msg.SiteFanoutCalls, Is.EqualTo(1));
        Assert.That(msg.PublishedCommandTypes[0], Is.EqualTo("restart"));
        Assert.That(result.Value!.OnlineAgents, Is.EqualTo(1));
    }

    [Test]
    public async Task Restart_WithNoOnlineAgents_Fails()
    {
        var offline = NewAgent(Guid.NewGuid(), AgentStatus.Offline);
        var msg = new FakeAgentMessaging();
        var handler = new SiteRestartCommandHandler(msg, new FakeSiteRepository(NewSite()), new FakeAgentRepository([offline]));

        var result = await handler.Handle(new SiteRestartCommand(_siteId, 15, false, null), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(msg.SiteFanoutCalls, Is.EqualTo(0));
    }

    // ── Shutdown ──

    [Test]
    public async Task Shutdown_WithOnlineAgents_PublishesFanoutWithShutdown()
    {
        var online = NewAgent(Guid.NewGuid(), AgentStatus.Online);
        var msg = new FakeAgentMessaging();
        var handler = new SiteShutdownCommandHandler(msg, new FakeSiteRepository(NewSite()), new FakeAgentRepository([online]));

        var result = await handler.Handle(new SiteShutdownCommand(_siteId, 30, false, null), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(msg.PublishedCommandTypes[0], Is.EqualTo("shutdown"));
    }

    // ── Wake-on-LAN ──

    [Test]
    public async Task WakeOnLan_SendsPacketForAllMacsOfOfflineAgents()
    {
        var online = NewAgent(Guid.NewGuid(), AgentStatus.Online);
        var offline1Id = Guid.NewGuid();
        var offline1 = NewAgent(offline1Id, AgentStatus.Offline, "AA:BB:CC:DD:EE:01");
        var offline2 = NewAgent(Guid.NewGuid(), AgentStatus.Offline, "AA:BB:CC:DD:EE:02");

        var components = new Dictionary<Guid, AgentHardwareComponents>
        {
            [offline1Id] = new()
            {
                NetworkAdapters = new List<NetworkAdapterInfo>
                {
                    new() { MacAddress = "AA:BB:CC:DD:EE:AA" },
                    new() { MacAddress = "AA:BB:CC:DD:EE:01" },
                }
            }
        };

        var msg = new FakeAgentMessaging();
        var handler = new SiteWakeOnLanCommandHandler(
            msg,
            new FakeSiteRepository(NewSite()),
            new FakeAgentRepository([online, offline1, offline2]),
            new FakeHardwareRepository(components));

        var result = await handler.Handle(new SiteWakeOnLanCommand(_siteId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var dto = result.Value!;
        // 2 agentes alvo (offline1 e offline2).
        Assert.That(dto.TargetCount, Is.EqualTo(2));
        // MACs: offline1 (AA..01) + adaptador (AA..AA) + offline2 (AA..02) = 3 únicos.
        Assert.That(dto.MacAddresses.Count, Is.EqualTo(3));
        Assert.That(dto.OnlineRelayCount, Is.EqualTo(1));
        Assert.That(msg.SiteFanoutCalls, Is.EqualTo(3));
        Assert.That(msg.PublishedCommandTypes.All(t => t == "wakeonlan"), Is.True);
    }

    [Test]
    public async Task WakeOnLan_WithNoOnlineAgents_Fails()
    {
        var offline = NewAgent(Guid.NewGuid(), AgentStatus.Offline, "AA:BB:CC:DD:EE:01");
        var msg = new FakeAgentMessaging();
        var handler = new SiteWakeOnLanCommandHandler(
            msg,
            new FakeSiteRepository(NewSite()),
            new FakeAgentRepository([offline]),
            new FakeHardwareRepository([]));

        var result = await handler.Handle(new SiteWakeOnLanCommand(_siteId), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
    }
}