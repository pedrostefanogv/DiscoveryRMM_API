using System.Collections.Concurrent;
using System.Text.Json;
using Discovery.Api.DependencyInjection;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class NatsAgentMessagingScopeAndSubjectTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static NatsAgentMessaging CreateMessaging(
        IServiceScopeFactory scopeFactory,
        List<(string Subject, string Payload)>? published = null)
    {
        var normalizer = new DashboardEventContractNormalizer(
            Options.Create(new RealtimeContractOptions()),
            TimeProvider.System,
            NullLogger<DashboardEventContractNormalizer>.Instance);

        return new NatsAgentMessaging(
            connection: null,
            scopeFactory: scopeFactory,
            contractNormalizer: normalizer,
            globalPongOptions: new StubOptionsMonitor<NatsGlobalPongOptions>(new NatsGlobalPongOptions()),
            logger: NullLogger<NatsAgentMessaging>.Instance,
            streamProvisioner: null,
            publishOverride: (subject, payload, _, _) =>
            {
                if (published is not null)
                {
                    lock (published)
                    {
                        published.Add((subject, payload));
                    }
                }

                return ValueTask.CompletedTask;
            });
    }

    [Test]
    public async Task Heartbeat_CreatesAndDisposesShortLivedScopePerMessage()
    {
        var scopeFactory = new CountingScopeFactory(wasTransition: false, isTrashed: false);
        var published = new List<(string Subject, string Payload)>();
        var messaging = CreateMessaging(scopeFactory, published);

        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        for (var i = 0; i < 3; i++)
        {
            var agentId = Guid.NewGuid();
            var subject = NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "heartbeat");
            var payload = JsonSerializer.Serialize(new AgentHeartbeat(agentId, clientId, siteId), CamelCaseJson);

            await messaging.HandleHeartbeatMessageAsync(subject, payload, CancellationToken.None);
            Assert.That(scopeFactory.ActiveUnDisposedScopes, Is.EqualTo(0),
                "Each heartbeat message must dispose its AsyncServiceScope before returning.");
        }

        Assert.That(scopeFactory.CreatedScopesCount, Is.GreaterThanOrEqualTo(6),
            "Each distinct agent triggers auth scope + heartbeat cache scope.");
        Assert.That(scopeFactory.DisposedScopesCount, Is.EqualTo(scopeFactory.CreatedScopesCount));
        Assert.That(scopeFactory.DistinctHeartbeatCacheInstances, Is.EqualTo(3));
        Assert.That(published, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Heartbeat_ConcurrentTasks_DoNotShareScopeOrDbContext()
    {
        var dbName = $"concurrent-hw-{Guid.NewGuid():N}";
        var scopeFactory = new CountingScopeFactory(
            wasTransition: false,
            isTrashed: false,
            dbContextFactory: () =>
            {
                var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
                    .UseInMemoryDatabase(dbName)
                    .Options;
                return new HardwareTestDbContext(options);
            });
        var published = new List<(string Subject, string Payload)>();
        var messaging = CreateMessaging(scopeFactory, published);

        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agent1 = Guid.NewGuid();
        var agent2 = Guid.NewGuid();
        var agent3 = Guid.NewGuid();

        // Warm auth cache before concurrent burst
        foreach (var a in new[] { agent1, agent2, agent3 })
        {
            await messaging.HandleHeartbeatMessageAsync(
                NatsSubjectBuilder.AgentSubject(clientId, siteId, a, "heartbeat"),
                JsonSerializer.Serialize(new AgentHeartbeat(a)),
                CancellationToken.None);
        }

        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(messaging.HandleHeartbeatMessageAsync(
                NatsSubjectBuilder.AgentSubject(clientId, siteId, agent1, "heartbeat"),
                JsonSerializer.Serialize(new AgentHeartbeat(agent1)),
                CancellationToken.None));

            tasks.Add(messaging.HandleResultMessageAsync(
                NatsSubjectBuilder.AgentSubject(clientId, siteId, agent2, "result"),
                JsonSerializer.Serialize(new { commandId = Guid.NewGuid(), exitCode = 0, output = "ok" }),
                CancellationToken.None));

            tasks.Add(messaging.HandleHardwareMessageAsync(
                NatsSubjectBuilder.AgentSubject(clientId, siteId, agent3, "hardware"),
                JsonSerializer.Serialize(new { hardware = new { manufacturer = "Dell", model = "OptiPlex" } }),
                CancellationToken.None));
        }

        await Task.WhenAll(tasks);

        Assert.That(scopeFactory.ActiveUnDisposedScopes, Is.EqualTo(0));
        Assert.That(scopeFactory.DisposedScopesCount, Is.EqualTo(scopeFactory.CreatedScopesCount));
    }

    [Test]
    public async Task Heartbeat_NoTransition_ShortCircuitsIsTrashedAgentAsync()
    {
        var scopeFactory = new CountingScopeFactory(wasTransition: true, isTrashed: false);
        var published = new List<(string Subject, string Payload)>();
        var messaging = CreateMessaging(scopeFactory, published);

        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var subject = NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "heartbeat");

        // Warm _authCache with initial transition (PeerId == null)
        await messaging.HandleHeartbeatMessageAsync(
            subject,
            JsonSerializer.Serialize(new AgentHeartbeat(agentId, PeerId: null), CamelCaseJson),
            CancellationToken.None);

        scopeFactory.ResetCounters();
        scopeFactory.WasTransition = false;
        published.Clear();

        for (var i = 0; i < 5; i++)
        {
            await messaging.HandleHeartbeatMessageAsync(
                subject,
                JsonSerializer.Serialize(new AgentHeartbeat(agentId, PeerId: "12D3KooWTestPeer"), CamelCaseJson),
                CancellationToken.None);
        }

        Assert.That(scopeFactory.GetByIdForDecommissionCalls, Is.EqualTo(0),
            "Steady-state heartbeats (wasTransition=false) must NEVER call IsTrashedAgentAsync.");
        Assert.That(published.Any(p => p.Subject.EndsWith(".p2p.events", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task Heartbeat_TransitionWithoutPeerId_ShortCircuitsIsTrashedAgentAsync()
    {
        var scopeFactory = new CountingScopeFactory(wasTransition: true, isTrashed: false);
        var messaging = CreateMessaging(scopeFactory);

        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var subject = NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "heartbeat");

        // Warm _authCache
        await messaging.HandleHeartbeatMessageAsync(
            subject,
            JsonSerializer.Serialize(new AgentHeartbeat(agentId), CamelCaseJson),
            CancellationToken.None);

        scopeFactory.ResetCounters();
        scopeFactory.WasTransition = true;

        await messaging.HandleHeartbeatMessageAsync(
            subject,
            JsonSerializer.Serialize(new AgentHeartbeat(agentId, PeerId: "   "), CamelCaseJson),
            CancellationToken.None);

        Assert.That(scopeFactory.GetByIdForDecommissionCalls, Is.EqualTo(0),
            "Transition with empty/whitespace PeerId must short-circuit before calling IsTrashedAgentAsync.");
    }

    [Test]
    public async Task Heartbeat_TransitionWithPeerId_ChecksIsTrashedAndPublishesOnlyIfNotTrashed()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var subject = NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "heartbeat");

        // Non-trashed agent -> checks IsTrashed once and publishes peer.online
        var scopeFactoryOk = new CountingScopeFactory(wasTransition: true, isTrashed: false);
        var publishedOk = new List<(string Subject, string Payload)>();
        var messagingOk = CreateMessaging(scopeFactoryOk, publishedOk);

        await messagingOk.HandleHeartbeatMessageAsync(subject, JsonSerializer.Serialize(new AgentHeartbeat(agentId), CamelCaseJson), CancellationToken.None);
        scopeFactoryOk.ResetCounters();
        publishedOk.Clear();

        await messagingOk.HandleHeartbeatMessageAsync(
            subject,
            JsonSerializer.Serialize(new AgentHeartbeat(agentId, PeerId: "12D3KooWActive"), CamelCaseJson),
            CancellationToken.None);

        Assert.That(scopeFactoryOk.GetByIdForDecommissionCalls, Is.EqualTo(1));
        Assert.That(publishedOk.Any(p => p.Subject == NatsSubjectBuilder.P2pClientEventsSubject(clientId)), Is.True);

        // Trashed agent -> checks IsTrashed once and suppresses peer.online
        var scopeFactoryTrashed = new CountingScopeFactory(wasTransition: true, isTrashed: true);
        var publishedTrashed = new List<(string Subject, string Payload)>();
        var messagingTrashed = CreateMessaging(scopeFactoryTrashed, publishedTrashed);

        await messagingTrashed.HandleHeartbeatMessageAsync(subject, JsonSerializer.Serialize(new AgentHeartbeat(agentId), CamelCaseJson), CancellationToken.None);
        scopeFactoryTrashed.ResetCounters();
        publishedTrashed.Clear();

        await messagingTrashed.HandleHeartbeatMessageAsync(
            subject,
            JsonSerializer.Serialize(new AgentHeartbeat(agentId, PeerId: "12D3KooWTrashed"), CamelCaseJson),
            CancellationToken.None);

        Assert.That(scopeFactoryTrashed.GetByIdForDecommissionCalls, Is.EqualTo(1));
        Assert.That(publishedTrashed.Any(p => p.Subject.EndsWith(".p2p.events", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task AgentHardwareRepository_UpsertAsync_UsesAsNoTrackingAndLeavesTrackerClean()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new HardwareTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var agentId = Guid.NewGuid();
        var repo = new AgentHardwareRepository(db);

        await repo.UpsertAsync(new AgentHardwareInfo
        {
            AgentId = agentId,
            Manufacturer = "Dell",
            Model = "Latitude 5420",
            Processor = "Intel i7"
        });

        Assert.That(db.ChangeTracker.Entries<AgentHardwareInfo>().Count(), Is.EqualTo(0),
            "Insert UpsertAsync must leave ChangeTracker clean.");

        await repo.UpsertAsync(new AgentHardwareInfo
        {
            AgentId = agentId,
            Manufacturer = "Dell",
            Model = "Latitude 5440",
            Processor = "Intel Ultra 7"
        });

        Assert.That(db.ChangeTracker.Entries<AgentHardwareInfo>().Count(), Is.EqualTo(0),
            "Update UpsertAsync must use AsNoTracking and detach entity after SaveChanges.");

        var fetched = await repo.GetByAgentIdAsync(agentId);
        Assert.That(fetched, Is.Not.Null);
        Assert.That(fetched!.Model, Is.EqualTo("Latitude 5440"));
        Assert.That(fetched.Processor, Is.EqualTo("Intel Ultra 7"));
    }

    [Test]
    public async Task Heartbeat_SpoofedPayloadTenantIds_OverriddenBySubjectIdentity()
    {
        var scopeFactory = new CountingScopeFactory(wasTransition: true, isTrashed: false);
        var published = new List<(string Subject, string Payload)>();
        var messaging = CreateMessaging(scopeFactory, published);

        var realClient = Guid.NewGuid();
        var realSite = Guid.NewGuid();
        var realAgent = Guid.NewGuid();

        var spoofClient = Guid.NewGuid();
        var spoofSite = Guid.NewGuid();
        var spoofAgent = Guid.NewGuid();

        var subject = NatsSubjectBuilder.AgentSubject(realClient, realSite, realAgent, "heartbeat");
        var payload = JsonSerializer.Serialize(new AgentHeartbeat(
            AgentId: spoofAgent,
            ClientId: spoofClient,
            SiteId: spoofSite,
            PeerId: "12D3KooWSpoofAttempt"), CamelCaseJson);

        await messaging.HandleHeartbeatMessageAsync(subject, payload, CancellationToken.None);

        Assert.That(scopeFactory.RecordedHeartbeats, Has.Count.EqualTo(1));
        var cached = scopeFactory.RecordedHeartbeats[0];
        Assert.That(cached.AgentId, Is.EqualTo(realAgent));
        Assert.That(cached.ClientId, Is.EqualTo(realClient));
        Assert.That(cached.SiteId, Is.EqualTo(realSite));

        Assert.That(published.Any(p => p.Subject == NatsSubjectBuilder.P2pClientEventsSubject(realClient)), Is.True);
        Assert.That(published.Any(p => p.Subject == NatsSubjectBuilder.DashboardSubject(realClient, realSite)), Is.True);
        Assert.That(published.Any(p =>
            p.Subject.Contains(spoofClient.ToString(), StringComparison.OrdinalIgnoreCase)
            || p.Subject.Contains(spoofSite.ToString(), StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public async Task ResultAndHardware_UseSubjectTenantWithoutDbTenantLookups()
    {
        var scopeFactory = new CountingScopeFactory(wasTransition: false, isTrashed: false);
        var published = new List<(string Subject, string Payload)>();
        var messaging = CreateMessaging(scopeFactory, published);

        var realClient = Guid.NewGuid();
        var realSite = Guid.NewGuid();
        var realAgent = Guid.NewGuid();

        // Warm _authCache
        await messaging.HandleHeartbeatMessageAsync(
            NatsSubjectBuilder.AgentSubject(realClient, realSite, realAgent, "heartbeat"),
            JsonSerializer.Serialize(new AgentHeartbeat(realAgent)),
            CancellationToken.None);

        scopeFactory.ResetCounters();
        published.Clear();

        await messaging.HandleResultMessageAsync(
            NatsSubjectBuilder.AgentSubject(realClient, realSite, realAgent, "result"),
            JsonSerializer.Serialize(new { commandId = Guid.NewGuid(), exitCode = 0, output = "done" }),
            CancellationToken.None);

        await messaging.HandleHardwareMessageAsync(
            NatsSubjectBuilder.AgentSubject(realClient, realSite, realAgent, "hardware"),
            JsonSerializer.Serialize(new { hardware = new { manufacturer = "HP", model = "EliteBook" } }),
            CancellationToken.None);

        Assert.That(scopeFactory.GetByIdForDecommissionCalls, Is.EqualTo(0));
        Assert.That(scopeFactory.SiteGetByIdCalls, Is.EqualTo(0));

        var expectedDashboardSubject = NatsSubjectBuilder.DashboardSubject(realClient, realSite);
        Assert.That(published, Has.Count.EqualTo(2));
        Assert.That(published.All(p => p.Subject == expectedDashboardSubject), Is.True);
        Assert.That(published[0].Payload, Does.Contain("CommandCompleted"));
        Assert.That(published[1].Payload, Does.Contain("AgentHardwareReported"));
    }

    [Test]
    public async Task InvalidSubject_IsRejectedWithoutProcessing()
    {
        var scopeFactory = new CountingScopeFactory(wasTransition: false, isTrashed: false);
        var published = new List<(string Subject, string Payload)>();
        var messaging = CreateMessaging(scopeFactory, published);

        var id = Guid.NewGuid();
        await messaging.HandleHeartbeatMessageAsync($"agent.{id}.heartbeat", "{}", CancellationToken.None);
        await messaging.HandleResultMessageAsync($"tenant.not-a-guid.site.{id}.agent.{id}.result", "{}", CancellationToken.None);
        await messaging.HandleHardwareMessageAsync($"tenant.{id}.site.{id}.agent.{Guid.Empty}.hardware", "{}", CancellationToken.None);
        await messaging.HandleSupportChatMessageAsync($"tenant.{id}.site.{id}.agent.{id}", "{}", CancellationToken.None);

        Assert.That(scopeFactory.CreatedScopesCount, Is.EqualTo(0));
        Assert.That(scopeFactory.RecordedHeartbeats, Is.Empty);
        Assert.That(published, Is.Empty);
    }

    [Test]
    public async Task SupportChat_SpoofedPayloadAgentId_OverriddenBySubjectIdentity()
    {
        var scopeFactory = new CountingScopeFactory(wasTransition: false, isTrashed: false);
        var messaging = CreateMessaging(scopeFactory);

        var realClient = Guid.NewGuid();
        var realSite = Guid.NewGuid();
        var realAgent = Guid.NewGuid();
        var spoofAgent = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var subject = NatsSubjectBuilder.SupportChatAgentSubject(realClient, realSite, realAgent);
        var envelope = new TicketChatEnvelope
        {
            EventType = TicketChatEventTypes.Message,
            SessionId = sessionId,
            AgentId = spoofAgent,
            AuthorKind = "enduser",
            Body = "hello",
            ClientMessageId = Guid.NewGuid()
        };

        await messaging.HandleSupportChatMessageAsync(
            subject,
            JsonSerializer.Serialize(envelope, CamelCaseJson),
            CancellationToken.None);

        Assert.That(scopeFactory.RecordedChatEnvelopes, Has.Count.EqualTo(1));
        var recorded = scopeFactory.RecordedChatEnvelopes[0];
        Assert.That(recorded.AgentId, Is.EqualTo(realAgent));
        Assert.That(recorded.Envelope.AgentId, Is.EqualTo(realAgent));
        Assert.That(recorded.Envelope.SessionId, Is.EqualTo(sessionId));
        Assert.That(scopeFactory.ActiveUnDisposedScopes, Is.EqualTo(0));
    }

    [Test]
    public async Task SupportChat_EnvelopeDeConversaAvulsaComIdentidadeVazia_EhIngerido()
    {
        // BUG REAL (2026-11-20 01:00, sessão b066d11d): o agent em Go publicava
        // "ticketId":"" e "agentId":"" nas conversas AVULSAS. O System.Text.Json
        // não converte "" para Guid?/Guid e lançava JsonException ANTES de
        // qualquer validação — o envelope era descartado pelo catch do
        // HandleSupportChatMessageAsync e a mensagem do usuário final nunca era
        // persistida. O console do suporte ficava sem ela, embora o agent
        // tivesse recebido PubAck do JetStream.
        //
        // O payload abaixo é o do log da API em produção, reproduzido cru.
        var scopeFactory = new CountingScopeFactory(wasTransition: false, isTrashed: false);
        var messaging = CreateMessaging(scopeFactory);

        var realClient = Guid.NewGuid();
        var realSite = Guid.NewGuid();
        var realAgent = Guid.NewGuid();
        var sessionId = Guid.Parse("b066d11d-84dc-401f-ba23-ba907fb5c5d4");

        var subject = NatsSubjectBuilder.SupportChatAgentSubject(realClient, realSite, realAgent);
        const string rawPayload = """
        {"eventType":"Message","sessionId":"b066d11d-84dc-401f-ba23-ba907fb5c5d4","ticketId":"","agentId":"","authorKind":"enduser","body":"Teste do chat","clientMessageId":"0d1b6a5f-9d3a-4a2e-9d55-1c2f9a2f7b41"}
        """;

        await messaging.HandleSupportChatMessageAsync(subject, rawPayload, CancellationToken.None);

        Assert.That(scopeFactory.RecordedChatEnvelopes, Has.Count.EqualTo(1),
            "o envelope de conversa avulsa precisa chegar ao serviço de ingestão");
        var recorded = scopeFactory.RecordedChatEnvelopes[0];
        Assert.That(recorded.Envelope.SessionId, Is.EqualTo(sessionId));
        Assert.That(recorded.Envelope.TicketId, Is.Null, "conversa avulsa não tem chamado");
        Assert.That(recorded.Envelope.Body, Is.EqualTo("Teste do chat"));
        Assert.That(recorded.AgentId, Is.EqualTo(realAgent), "o AgentId vem do SUBJECT, não do payload");
        Assert.That(recorded.Envelope.AgentId, Is.EqualTo(realAgent));
    }

    [Test]
    public async Task SupportChat_EnvelopeSemCamposDeIdentidade_EhIngerido()
    {
        // Formato NOVO do agent (omitempty): os campos de identidade não vêm.
        var scopeFactory = new CountingScopeFactory(wasTransition: false, isTrashed: false);
        var messaging = CreateMessaging(scopeFactory);

        var realAgent = Guid.NewGuid();
        var subject = NatsSubjectBuilder.SupportChatAgentSubject(Guid.NewGuid(), Guid.NewGuid(), realAgent);
        const string rawPayload = """
        {"eventType":"Message","sessionId":"b066d11d-84dc-401f-ba23-ba907fb5c5d4","authorKind":"enduser","body":"oi"}
        """;

        await messaging.HandleSupportChatMessageAsync(subject, rawPayload, CancellationToken.None);

        Assert.That(scopeFactory.RecordedChatEnvelopes, Has.Count.EqualTo(1));
        Assert.That(scopeFactory.RecordedChatEnvelopes[0].Envelope.TicketId, Is.Null);
        Assert.That(scopeFactory.RecordedChatEnvelopes[0].AgentId, Is.EqualTo(realAgent));
    }

    [Test]
    public void DiRegistration_RegistersIAgentMessagingAsSingleton()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nats:Url"] = "nats://localhost:4222"
            })
            .Build();

        services.AddDiscoveryAutoRegisteredServices();
        services.AddDiscoveryNats(config);

        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAgentMessaging));
        Assert.That(descriptor, Is.Not.Null);
        Assert.That(descriptor!.Lifetime, Is.EqualTo(ServiceLifetime.Singleton));
        Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(NatsAgentMessaging)));
    }

    private sealed class StubOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class CountingScopeFactory(
        bool wasTransition,
        bool isTrashed,
        Func<DiscoveryDbContext>? dbContextFactory = null) : IServiceScopeFactory
    {
        private int _created;
        private int _disposed;
        private int _decommissionCalls;
        private int _siteGetByIdCalls;
        private readonly ConcurrentDictionary<int, byte> _cacheInstances = new();

        public bool WasTransition { get; set; } = wasTransition;
        public bool IsTrashed { get; set; } = isTrashed;
        public List<AgentHeartbeat> RecordedHeartbeats { get; } = [];
        public List<(TicketChatEnvelope Envelope, Guid AgentId)> RecordedChatEnvelopes { get; } = [];

        public int CreatedScopesCount => Volatile.Read(ref _created);
        public int DisposedScopesCount => Volatile.Read(ref _disposed);
        public int ActiveUnDisposedScopes => CreatedScopesCount - DisposedScopesCount;
        public int GetByIdForDecommissionCalls => Volatile.Read(ref _decommissionCalls);
        public int SiteGetByIdCalls => Volatile.Read(ref _siteGetByIdCalls);
        public int DistinctHeartbeatCacheInstances => _cacheInstances.Count;

        public void ResetCounters()
        {
            Interlocked.Exchange(ref _decommissionCalls, 0);
            Interlocked.Exchange(ref _siteGetByIdCalls, 0);
        }

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _created);
            var sp = new CountingServiceProvider(this, dbContextFactory?.Invoke());
            return new CountingAsyncScope(this, sp);
        }

        private void OnScopeDisposed() => Interlocked.Increment(ref _disposed);
        private void OnDecommissionLookup() => Interlocked.Increment(ref _decommissionCalls);
        private void OnSiteGetByIdLookup() => Interlocked.Increment(ref _siteGetByIdCalls);
        private void RecordCacheInstance(object instance) => _cacheInstances.TryAdd(instance.GetHashCode(), 1);

        private sealed class CountingAsyncScope(CountingScopeFactory owner, CountingServiceProvider sp) : IServiceScope, IAsyncDisposable
        {
            private int _disposedFlag;
            public IServiceProvider ServiceProvider => sp;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposedFlag, 1) == 0)
                {
                    sp.DisposeSync();
                    owner.OnScopeDisposed();
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposedFlag, 1) == 0)
                {
                    await sp.DisposeAsync();
                    owner.OnScopeDisposed();
                }
            }
        }

        private sealed class CountingServiceProvider(CountingScopeFactory owner, DiscoveryDbContext? db) : IServiceProvider, IAsyncDisposable
        {
            private StubHeartbeatCacheService? _hbCache;
            private readonly StubAgentRepo _agentRepo = new(owner);
            private readonly StubSiteRepo _siteRepo = new(owner);
            private readonly StubAgentAuthService _authService = new();
            private readonly StubCommandRepo _commandRepo = new();
            private readonly StubTicketChatIngestService _chatIngest = new(owner);
            private readonly IAgentHardwareRepository _hardwareRepo = db is not null
                ? new AgentHardwareRepository(db)
                : new StubHardwareRepo();

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IHeartbeatCacheService)) return _hbCache ??= new StubHeartbeatCacheService(owner);
                if (serviceType == typeof(IAgentRepository)) return _agentRepo;
                if (serviceType == typeof(ISiteRepository)) return _siteRepo;
                if (serviceType == typeof(IAgentAuthService)) return _authService;
                if (serviceType == typeof(ICommandRepository)) return _commandRepo;
                if (serviceType == typeof(ITicketChatIngestService)) return _chatIngest;
                if (serviceType == typeof(IAgentHardwareRepository)) return _hardwareRepo;
                return null;
            }

            public void DisposeSync() => db?.Dispose();

            public async ValueTask DisposeAsync()
            {
                if (db is not null)
                    await db.DisposeAsync();
            }
        }

        private sealed class StubHeartbeatCacheService : IHeartbeatCacheService
        {
            private readonly CountingScopeFactory _owner;

            public StubHeartbeatCacheService(CountingScopeFactory owner)
            {
                _owner = owner;
                _owner.RecordCacheInstance(this);
            }

            public Task<bool> SetHeartbeatAsync(AgentHeartbeat heartbeat, AgentStatus status, CancellationToken ct = default)
            {
                lock (_owner.RecordedHeartbeats)
                    _owner.RecordedHeartbeats.Add(heartbeat);
                return Task.FromResult(_owner.WasTransition);
            }

            public Task<HeartbeatCacheEntry?> GetHeartbeatAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult<HeartbeatCacheEntry?>(null);
            public Task<IReadOnlyList<HeartbeatCacheEntry>> GetActiveHeartbeatsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HeartbeatCacheEntry>>([]);
            public Task RemoveHeartbeatAsync(Guid agentId, CancellationToken ct = default) => Task.CompletedTask;
            public Task<bool> IsOnlineAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult(false);
            public Task<IReadOnlyList<Guid>> DetectExpiredAsync(IReadOnlyList<Guid> knownOnlineAgentIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
            public Task SetTransportAsync(Guid agentId, string transport, CancellationToken ct = default) => Task.CompletedTask;
            public Task<string?> GetTransportAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        }

        private sealed class StubAgentAuthService : IAgentAuthService
        {
            public Task<IEnumerable<AgentToken>> GetTokensByAgentIdAsync(Guid agentId)
                => Task.FromResult<IEnumerable<AgentToken>>([new AgentToken { Id = Guid.NewGuid(), AgentId = agentId }]);

            public Task<(AgentToken Token, string RawToken)> CreateTokenAsync(Guid agentId, string? description) => throw new NotSupportedException();
            public Task<AgentToken?> ValidateTokenAsync(string rawToken) => throw new NotSupportedException();
            public Task RevokeTokenAsync(Guid tokenId) => throw new NotSupportedException();
            public Task RevokeAllTokensAsync(Guid agentId) => throw new NotSupportedException();
            public Task<bool> TryAcquireNatsSessionAsync(Guid tokenId, Guid agentId, string userNkey, TimeSpan sessionTtl) => throw new NotSupportedException();
            public Task ReleaseNatsSessionAsync(Guid tokenId) => throw new NotSupportedException();
            public Task UpdateLastNatsConnectedAsync(Guid tokenId) => throw new NotSupportedException();
        }

        private sealed class StubAgentRepo(CountingScopeFactory owner) : IAgentRepository
        {
            public Task<Agent?> GetByIdForDecommissionAsync(Guid id)
            {
                owner.OnDecommissionLookup();
                return Task.FromResult<Agent?>(new Agent
                {
                    Id = id,
                    DeletedAt = owner.IsTrashed ? DateTime.UtcNow : null
                });
            }

            public Task<Agent?> GetByIdAsync(Guid id) => throw new NotSupportedException();
            public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotSupportedException();
            public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotSupportedException();
            public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotSupportedException();
            public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();
            public Task UpdateAsync(Agent agent) => throw new NotSupportedException();
            public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => throw new NotSupportedException();
            public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<Agent>> GetOnlineIncludingTrashedAsync(CancellationToken ct = default) => throw new NotSupportedException();
            public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
            public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => throw new NotSupportedException();
            public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotSupportedException();
            public Task DeleteAsync(Guid id) => throw new NotSupportedException();
            public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => throw new NotSupportedException();
        }

        private sealed class StubSiteRepo(CountingScopeFactory owner) : ISiteRepository
        {
            public Task<Site?> GetByIdAsync(Guid id)
            {
                owner.OnSiteGetByIdLookup();
                return Task.FromResult<Site?>(null);
            }

            public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotSupportedException();
            public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => throw new NotSupportedException();
            public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) => throw new NotSupportedException();
            public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false) => throw new NotSupportedException();
            public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
            public Task UpdateAsync(Site site) => throw new NotSupportedException();
            public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        }

        private sealed class StubCommandRepo : ICommandRepository
        {
            public Task<AgentCommand?> GetByIdAsync(Guid id) => Task.FromResult<AgentCommand?>(null);
            public Task UpdateStatusAsync(Guid id, CommandStatus status, string? result, int? exitCode, string? errorMessage) => Task.CompletedTask;
            public Task<IEnumerable<AgentCommand>> GetPendingByAgentIdAsync(Guid agentId) => throw new NotSupportedException();
            public Task<IReadOnlyList<AgentCommand>> GetRedeliveryCandidatesAsync(IReadOnlyCollection<Guid> agentIds, DateTime createdAfterUtc, DateTime staleBeforeUtc, int limit, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<AgentCommand>> GetExpiredUnconfirmedAsync(DateTime createdBeforeUtc, int limit, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<IEnumerable<AgentCommand>> GetByAgentIdAsync(Guid agentId, int limit = 50) => throw new NotSupportedException();
            public Task<AgentCommand> CreateAsync(AgentCommand command) => throw new NotSupportedException();
            public Task<int> CancelPendingByAgentAndTypeAsync(Guid agentId, CommandType commandType, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        }

        private sealed class StubHardwareRepo : IAgentHardwareRepository
        {
            public Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null) => Task.CompletedTask;
            public Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId) => throw new NotSupportedException();
            public Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId) => throw new NotSupportedException();
            public Task<IReadOnlyDictionary<Guid, AgentHardwareInfo>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<IReadOnlyDictionary<Guid, IReadOnlyList<DiskInfo>>> GetDisksByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default) => throw new NotSupportedException();
        }

        private sealed class StubTicketChatIngestService(CountingScopeFactory owner) : ITicketChatIngestService
        {
            public Task<bool> HandleAgentEnvelopeAsync(TicketChatEnvelope envelope, Guid agentId, CancellationToken cancellationToken = default)
            {
                lock (owner.RecordedChatEnvelopes)
                    owner.RecordedChatEnvelopes.Add((envelope, agentId));
                return Task.FromResult(true);
            }
        }
    }

    private sealed class HardwareTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(AgentHardwareInfo) };
            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null &&
                                     t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<AgentHardwareInfo>(entity => entity.HasKey(h => h.Id));
        }
    }
}
