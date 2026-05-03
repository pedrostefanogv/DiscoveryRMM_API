using Discovery.Api.Services;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.NKeys;

namespace Discovery.Tests;

/// <summary>
/// F4.5 — Testes negativos de isolamento multi-tenant: emissão de credenciais, ACLs
/// e remote debug tenant-scoped.
/// </summary>
[TestFixture]
public class NatsIsolationTests
{
    // ── Seed NATS válido gerado uma vez por sessão de testes ──────────────────
    private static readonly string TestAccountSeed =
        KeyPair.CreatePair(PrefixByte.Account).GetSeed();

    // ─────────────────────────────────────────────────────────────────────────
    // Grupo 1: Isolamento a nível de subject (NatsSubjectBuilder — sem deps)
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void CrossTenant_AgentSubjects_NeverOverlap()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();

        var subjectsA = AgentSubjectSet(clientA, siteA, agentA);
        var subjectsB = AgentSubjectSet(clientB, siteB, agentB);

        Assert.That(subjectsA.Intersect(subjectsB), Is.Empty,
            "Subjects de agentes de tenants distintos não devem se sobrepor.");
    }

    [Test]
    public void AgentSubjects_ContainNoLegacyPattern()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        foreach (var subject in AgentSubjectSet(clientId, siteId, agentId))
        {
            // O formato legado era "agent.{id}.*" — sem prefixo "tenant."
            Assert.That(subject, Does.StartWith("tenant."),
                $"Subject '{subject}' não segue o formato canônico tenant-scoped.");
        }
    }

    [Test]
    public void AgentSubjects_ContainExactlySeven_FourPublishThreeSubscribe()
    {
        var (pub, sub) = BuildAgentSubjectLists(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.That(pub, Has.Count.EqualTo(4),
            "Agente deve publicar exatamente em 4 subjects: heartbeat, result, hardware, remote-debug.log.");
        Assert.That(sub, Has.Count.EqualTo(3),
            "Agente deve assinar exatamente 3 subjects: command, sync.ping, p2p.discovery.");
    }

    [Test]
    public void AgentSubjects_ContainExpectedCanonicalMessageTypes()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var (pub, sub) = BuildAgentSubjectLists(clientId, siteId, agentId);
        var expectedPrefix = $"tenant.{clientId}.site.{siteId}.agent.{agentId}.";

        Assert.That(
            pub,
            Is.EquivalentTo(new[]
            {
                expectedPrefix + "heartbeat",
                expectedPrefix + "result",
                expectedPrefix + "hardware",
                expectedPrefix + "remote-debug.log",
            }),
            "Publish subjects devem conter somente os message types canônicos.");

        Assert.That(
            sub,
            Is.EquivalentTo(new[]
            {
                expectedPrefix + "command",
                expectedPrefix + "sync.ping",
            }),
            "Subscribe subjects devem conter somente os message types canônicos.");
    }

    [Test]
    public void AgentSubjects_DoNotContainLegacyAgentPrefix()
    {
        var (pub, sub) = BuildAgentSubjectLists(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var all = pub.Concat(sub).ToList();

        Assert.That(all.Any(subject => subject.StartsWith("agent.", StringComparison.OrdinalIgnoreCase)), Is.False,
            "Nenhum subject deve usar o prefixo legado agent.{id}.*.");
    }

    [Test]
    public void SameAgent_DifferentTenant_ProducesDifferentSubjects()
    {
        // Mesmo agentId mas clientes diferentes devem gerar subjects completamente distintos
        var sharedAgentId = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var siteA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var siteB = Guid.NewGuid();

        var subjectsA = AgentSubjectSet(clientA, siteA, sharedAgentId);
        var subjectsB = AgentSubjectSet(clientB, siteB, sharedAgentId);

        Assert.That(subjectsA.Intersect(subjectsB), Is.Empty,
            "Mesmo agentId com clientes diferentes deve produzir subjects distintos.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Grupo 2: Isolamento de ACLs emitidas por NatsCredentialsService (com fakes)
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AgentCredentials_PublishSubjects_BelongExclusivelyToThatAgent()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var service = BuildCredentialsService(agentId, siteId, clientId);
        var creds = await service.IssueForAgentAsync(agentId);

        Assert.That(creds.PublishSubjects, Is.Not.Empty);
        foreach (var s in creds.PublishSubjects)
        {
            Assert.That(s, Does.Contain(agentId.ToString()),
                $"Publish subject '{s}' não contém o agentId correto.");
            Assert.That(s, Does.Contain(clientId.ToString()),
                $"Publish subject '{s}' não contém o clientId correto.");
        }
    }

    [Test]
    public async Task AgentCredentials_SubscribeSubjects_BelongExclusivelyToThatAgent()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var service = BuildCredentialsService(agentId, siteId, clientId);
        var creds = await service.IssueForAgentAsync(agentId);

        Assert.That(creds.SubscribeSubjects, Is.Not.Empty);
        foreach (var s in creds.SubscribeSubjects)
        {
            Assert.That(s, Does.Contain(agentId.ToString()),
                $"Subscribe subject '{s}' não contém o agentId correto.");
        }
    }

    [Test]
    public async Task AgentCredentials_ContainNoLegacyUnscoped_AgentSubject()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var service = BuildCredentialsService(agentId, siteId, clientId);
        var creds = await service.IssueForAgentAsync(agentId);

        var allSubjects = creds.PublishSubjects.Concat(creds.SubscribeSubjects);
        foreach (var s in allSubjects)
        {
            Assert.That(s, Does.Not.StartWith($"agent.{agentId}"),
                $"Subject legado 'agent.{{agentId}}' encontrado: '{s}'.");
            Assert.That(s, Does.StartWith("tenant."),
                $"Subject não canônico sem prefixo 'tenant.': '{s}'.");
        }
    }

    [Test]
    public async Task UserCredentials_ClientScoped_ContainsOnlyOwnClientSubject()
    {
        var ownClientId = Guid.NewGuid();
        var otherClientId = Guid.NewGuid();

        var scope = new UserScopeAccess
        {
            HasGlobalAccess = false,
            AllowedClientIds = [ownClientId],
        };

        var service = BuildCredentialsService(Guid.NewGuid(), Guid.NewGuid(), ownClientId);
        var creds = await service.IssueForUserAsync(Guid.NewGuid(), scope, ownClientId, null);

        Assert.That(creds.SubscribeSubjects, Has.Count.EqualTo(1));
        Assert.That(creds.SubscribeSubjects[0], Does.Contain(ownClientId.ToString()),
            "Subject do usuário deve conter o clientId correto.");
        Assert.That(creds.SubscribeSubjects[0], Does.Not.Contain(otherClientId.ToString()),
            "Subject do usuário não deve conter clientId de outro tenant.");
    }

    [Test]
    public async Task UserCredentials_GlobalAccess_SubjectsAreWildcardOnly()
    {
        var scope = new UserScopeAccess { HasGlobalAccess = true };

        var service = BuildCredentialsService(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var creds = await service.IssueForUserAsync(Guid.NewGuid(), scope, null, null);

        Assert.That(creds.SubscribeSubjects, Is.Not.Empty);
        foreach (var s in creds.SubscribeSubjects)
        {
            Assert.That(s, Does.Contain("*"),
                $"Subject de usuário global '{s}' deveria ser wildcard — mas é específico de tenant.");
            Assert.That(s, Does.StartWith("tenant."),
                $"Subject global '{s}' deve usar o prefixo 'tenant.'.");
        }
    }

    [Test]
    public async Task UserCredentials_TwoClients_BothSubjectsPresent_NoCrossContamination()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var scope = new UserScopeAccess
        {
            HasGlobalAccess = false,
            AllowedClientIds = [clientA, clientB],
        };

        var service = BuildCredentialsService(Guid.NewGuid(), Guid.NewGuid(), clientA);
        var creds = await service.IssueForUserAsync(Guid.NewGuid(), scope, null, null);

        var clientASubjects = creds.SubscribeSubjects.Where(s => s.Contains(clientA.ToString())).ToList();
        var clientBSubjects = creds.SubscribeSubjects.Where(s => s.Contains(clientB.ToString())).ToList();

        Assert.That(clientASubjects, Is.Not.Empty,
            "Credenciais devem conter subject do clientA.");
        Assert.That(clientBSubjects, Is.Not.Empty,
            "Credenciais devem conter subject do clientB.");

        // os subjects de A não mencionam B e vice-versa
        Assert.That(clientASubjects.Any(s => s.Contains(clientB.ToString())), Is.False,
            "Subject do clientA não deve referenciar clientB.");
        Assert.That(clientBSubjects.Any(s => s.Contains(clientA.ToString())), Is.False,
            "Subject do clientB não deve referenciar clientA.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Grupo 3: RemoteDebugSessionManager — isolamento de sessão tenant-scoped
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void RemoteDebugSession_WrongAgent_IsRejected()
    {
        var manager = new RemoteDebugSessionManager();
        var correctAgent = Guid.NewGuid();
        var otherAgent = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var session = manager.StartSession(correctAgent, userId, Guid.NewGuid(), Guid.NewGuid(), "debug", 10);

        Assert.That(manager.TryGetSessionForAgent(session.SessionId, otherAgent, out _), Is.False,
            "Um agente diferente não deve ter acesso à sessão de debug de outro agente.");
    }

    [Test]
    public void RemoteDebugSession_WrongUser_IsRejected()
    {
        var manager = new RemoteDebugSessionManager();
        var agentId = Guid.NewGuid();
        var correctUser = Guid.NewGuid();
        var otherUser = Guid.NewGuid();

        var session = manager.StartSession(agentId, correctUser, Guid.NewGuid(), Guid.NewGuid(), "debug", 10);

        Assert.That(manager.TryGetSessionForUser(session.SessionId, otherUser, out _), Is.False,
            "Um usuário diferente não deve ter acesso à sessão de debug de outro usuário.");
    }

    [Test]
    public void RemoteDebugSession_NatsSubject_IsTenantScoped()
    {
        var manager = new RemoteDebugSessionManager();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var session = manager.StartSession(agentId, Guid.NewGuid(), clientId, siteId, "debug", 10);

        Assert.That(session.NatsSubject, Does.StartWith("tenant."),
            "O subject NATS da sessão deve usar o formato canônico tenant-scoped.");
        Assert.That(session.NatsSubject, Does.Contain(clientId.ToString()));
        Assert.That(session.NatsSubject, Does.Contain(siteId.ToString()));
        Assert.That(session.NatsSubject, Does.Contain(agentId.ToString()));
    }

    [Test]
    public void RemoteDebugSession_TwoAgents_SubjectsAreDistinct()
    {
        var manager = new RemoteDebugSessionManager();
        var sessionA = manager.StartSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "debug", 10);
        var sessionB = manager.StartSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "debug", 10);

        Assert.That(sessionA.NatsSubject, Is.Not.EqualTo(sessionB.NatsSubject),
            "Sessões de agentes distintos devem ter subjects NATS distintos.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static HashSet<string> AgentSubjectSet(Guid clientId, Guid siteId, Guid agentId)
    {
        var (pub, sub) = BuildAgentSubjectLists(clientId, siteId, agentId);
        return [.. pub, .. sub];
    }

    private static (List<string> Publish, List<string> Subscribe) BuildAgentSubjectLists(
        Guid clientId, Guid siteId, Guid agentId)
    {
        // Espelha exatamente o que NatsCredentialsService.BuildAgentSubjects faz
        return (
            Publish:
            [
                NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "heartbeat"),
                NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "result"),
                NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "hardware"),
                NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "remote-debug.log"),
            ],
            Subscribe:
            [
                NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "command"),
                NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "sync.ping"),
                NatsSubjectBuilder.P2pSiteDiscoverySubject(clientId, siteId),
            ]);
    }

    private static NatsCredentialsService BuildCredentialsService(
        Guid agentId, Guid siteId, Guid clientId)
    {
        var agent = new Agent { Id = agentId, SiteId = siteId };
        var site = new Site { Id = siteId, ClientId = clientId };

        return new NatsCredentialsService(
            agentRepository: new StubAgentRepository(agent),
            siteRepository: new StubSiteRepository(site),
            configurationService: new StubConfigurationService(),
            secretProtector: new PassthroughSecretProtector(),
            logger: NullLogger<NatsCredentialsService>.Instance);
    }

    // ── Fakes inline ──────────────────────────────────────────────────────────

    private sealed class StubConfigurationService : IConfigurationService
    {
        public Task<ServerConfiguration> GetServerConfigAsync() =>
            Task.FromResult(new ServerConfiguration
            {
                NatsAuthEnabled = true,
                NatsAccountSeed = TestAccountSeed,
                NatsAgentJwtTtlMinutes = 60,
                NatsUserJwtTtlMinutes = 60,
            });

        public Task<ServerConfiguration> UpdateServerAsync(ServerConfiguration config, string? updatedBy = null) => throw new NotImplementedException();
        public Task<ServerConfiguration> PatchServerAsync(Dictionary<string, object> updates, string? updatedBy = null) => throw new NotImplementedException();
        public Task<ServerConfiguration> ResetServerAsync(string? resetBy = null) => throw new NotImplementedException();
        public Task<ClientConfiguration?> GetClientConfigAsync(Guid clientId) => throw new NotImplementedException();
        public Task<ClientConfiguration> CreateClientConfigAsync(Guid clientId, ClientConfiguration config, string? createdBy = null) => throw new NotImplementedException();
        public Task<ClientConfiguration> UpdateClientAsync(Guid clientId, ClientConfiguration config, string? updatedBy = null) => throw new NotImplementedException();
        public Task<ClientConfiguration> PatchClientAsync(Guid clientId, Dictionary<string, object> updates, string? updatedBy = null) => throw new NotImplementedException();
        public Task DeleteClientConfigAsync(Guid clientId, string? deletedBy = null) => throw new NotImplementedException();
        public Task ResetClientPropertyAsync(Guid clientId, string propertyName, string? resetBy = null) => throw new NotImplementedException();
        public Task<SiteConfiguration?> GetSiteConfigAsync(Guid siteId) => throw new NotImplementedException();
        public Task<SiteConfiguration> CreateSiteConfigAsync(Guid siteId, SiteConfiguration config, string? createdBy = null) => throw new NotImplementedException();
        public Task<SiteConfiguration> UpdateSiteAsync(Guid siteId, SiteConfiguration config, string? updatedBy = null) => throw new NotImplementedException();
        public Task<SiteConfiguration> PatchSiteAsync(Guid siteId, Dictionary<string, object> updates, string? updatedBy = null) => throw new NotImplementedException();
        public Task DeleteSiteConfigAsync(Guid siteId, string? deletedBy = null) => throw new NotImplementedException();
        public Task ResetSitePropertyAsync(Guid siteId, string propertyName, string? resetBy = null) => throw new NotImplementedException();
        public Task<(bool IsValid, string[] Errors)> ValidateAsync(object config) => throw new NotImplementedException();
        public Task<(bool IsValid, string[] Errors)> ValidateJsonAsync(string objectType, string json) => throw new NotImplementedException();
    }

    private sealed class StubAgentRepository(Agent agent) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id) =>
            Task.FromResult(id == agent.Id ? (Agent?)agent : null);

        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotImplementedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotImplementedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotImplementedException();
        public Task<Agent> CreateAsync(Agent a) => throw new NotImplementedException();
        public Task UpdateAsync(Agent a) => throw new NotImplementedException();
        public Task UpdateStatusAsync(Guid id, Core.Enums.AgentStatus status, string? ipAddress) => throw new NotImplementedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubSiteRepository(Site site) : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id) =>
            Task.FromResult(id == site.Id ? (Site?)site : null);

        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotImplementedException();
        public Task<Site> CreateAsync(Site s) => throw new NotImplementedException();
        public Task UpdateAsync(Site s) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public bool IsEnabled => false;
        public bool IsProtected(string? value) => false;
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
        public string UnprotectOrSelf(string? value) => value ?? string.Empty;
    }
}
