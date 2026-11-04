using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Listagem de definições de sensor: nomes de cliente/site/agente resolvidos em
/// lote, filtros novos e paginação explícita (antes a lista cortava em 500 itens
/// sem avisar o cliente).
/// </summary>
public class SensorDefinitionServiceTests
{
    [Test]
    public async Task ListAsync_ShouldResolveClientSiteAndAgentNames()
    {
        await using var fixture = await CreateFixtureAsync();

        var page = await fixture.Service.ListAsync(
            scopeType: null,
            clientId: null,
            siteId: null,
            agentId: null,
            includeInactive: false,
            cancellationToken: CancellationToken.None);

        Assert.That(page.Total, Is.EqualTo(4));
        Assert.That(page.Count, Is.EqualTo(4));

        var clientSensor = page.Items.Single(item => item.Id == fixture.ClientSensor.Id);
        Assert.That(clientSensor.ClientName, Is.EqualTo("Cliente Alfa"));
        Assert.That(clientSensor.SiteName, Is.Null);
        Assert.That(clientSensor.AgentName, Is.Null);

        var siteSensor = page.Items.Single(item => item.Id == fixture.SiteSensor.Id);
        Assert.That(siteSensor.SiteName, Is.EqualTo("Site Beta"));
        Assert.That(siteSensor.ClientName, Is.Null);

        // Agente sem DisplayName cai no hostname.
        var agentSensor = page.Items.Single(item => item.Id == fixture.AgentSensor.Id);
        Assert.That(agentSensor.AgentName, Is.EqualTo("host-01"));

        // Agente com DisplayName usa o DisplayName.
        var namedAgentSensor = page.Items.Single(item => item.Id == fixture.NamedAgentSensor.Id);
        Assert.That(namedAgentSensor.AgentName, Is.EqualTo("Servidor de Arquivos"));
    }

    [Test]
    public async Task ListAsync_ShouldFilterBySearchSensorTypeAndIsActive()
    {
        await using var fixture = await CreateFixtureAsync();

        var byName = await fixture.Service.ListAsync(
            null, null, null, null, includeInactive: true,
            search: "disco", cancellationToken: CancellationToken.None);
        Assert.That(byName.Items.Select(item => item.Id), Is.EquivalentTo(new[] { fixture.ClientSensor.Id }));

        var byKey = await fixture.Service.ListAsync(
            null, null, null, null, includeInactive: true,
            search: "cpu_", cancellationToken: CancellationToken.None);
        Assert.That(byKey.Items.Select(item => item.Id), Is.EquivalentTo(new[] { fixture.SiteSensor.Id }));

        var byType = await fixture.Service.ListAsync(
            null, null, null, null, includeInactive: true,
            sensorType: SensorType.Cpu, cancellationToken: CancellationToken.None);
        Assert.That(byType.Items.Select(item => item.Id), Is.EquivalentTo(new[] { fixture.SiteSensor.Id }));

        var onlyInactive = await fixture.Service.ListAsync(
            null, null, null, null, includeInactive: true,
            isActive: false, cancellationToken: CancellationToken.None);
        Assert.That(onlyInactive.Items.Select(item => item.Id), Is.EquivalentTo(new[] { fixture.InactiveSensor.Id }));

        // Sem includeInactive o filtro base já restringe a ativos.
        var defaultList = await fixture.Service.ListAsync(
            null, null, null, null, includeInactive: false,
            cancellationToken: CancellationToken.None);
        Assert.That(defaultList.Items.Any(item => item.Id == fixture.InactiveSensor.Id), Is.False);
    }

    [Test]
    public async Task ListAsync_ShouldPageAndReportTotal()
    {
        await using var fixture = await CreateFixtureAsync();

        var firstPage = await fixture.Service.ListAsync(
            null, null, null, null, includeInactive: true,
            page: 1, pageSize: 2, cancellationToken: CancellationToken.None);

        Assert.That(firstPage.Total, Is.EqualTo(5));
        Assert.That(firstPage.Count, Is.EqualTo(2));
        Assert.That(firstPage.Page, Is.EqualTo(1));
        Assert.That(firstPage.PageSize, Is.EqualTo(2));

        var secondPage = await fixture.Service.ListAsync(
            null, null, null, null, includeInactive: true,
            page: 2, pageSize: 2, cancellationToken: CancellationToken.None);

        Assert.That(secondPage.Total, Is.EqualTo(5));
        Assert.That(secondPage.Count, Is.EqualTo(2));

        var firstIds = firstPage.Items.Select(item => item.Id).ToList();
        var secondIds = secondPage.Items.Select(item => item.Id).ToList();
        Assert.That(firstIds.Intersect(secondIds), Is.Empty);
        Assert.That(firstIds.Concat(secondIds).Distinct().Count(), Is.EqualTo(4));

        // A página 3 fecha as 5 definições.
        var thirdPage = await fixture.Service.ListAsync(
            null, null, null, null, includeInactive: true,
            page: 3, pageSize: 2, cancellationToken: CancellationToken.None);
        Assert.That(thirdPage.Total, Is.EqualTo(5));
        Assert.That(thirdPage.Count, Is.EqualTo(1));
        Assert.That(thirdPage.Items.Select(item => item.Id).Except(firstIds.Concat(secondIds)).ToList(), Has.Count.EqualTo(1));

        // Página além do fim devolve vazio, mas mantém o total.
        var beyond = await fixture.Service.ListAsync(
            null, null, null, null, includeInactive: true,
            page: 9, pageSize: 2, cancellationToken: CancellationToken.None);
        Assert.That(beyond.Items, Is.Empty);
        Assert.That(beyond.Total, Is.EqualTo(5));
    }

    [Test]
    public async Task ListAsync_ShouldClampPageSizeToMaximum()
    {
        await using var fixture = await CreateFixtureAsync();

        var page = await fixture.Service.ListAsync(
            null, null, null, null, includeInactive: true,
            pageSize: 10_000, cancellationToken: CancellationToken.None);

        Assert.That(page.PageSize, Is.EqualTo(SensorDefinitionService.MaxPageSize));
        Assert.That(page.Page, Is.EqualTo(1));
    }

    [Test]
    public async Task GetAsync_ShouldResolveScopeNames()
    {
        await using var fixture = await CreateFixtureAsync();

        var dto = await fixture.Service.GetAsync(fixture.AgentSensor.Id, CancellationToken.None);

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.AgentName, Is.EqualTo("host-01"));
    }

    private static async Task<SensorFixture> CreateFixtureAsync()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"sensor-definition-tests-{Guid.NewGuid():N}")
            .Options;

        var db = new TestDiscoveryDbContext(options);

        var now = DateTime.UtcNow;
        var client = new Client { Id = Guid.NewGuid(), Name = "Cliente Alfa", CreatedAt = now, UpdatedAt = now };
        var site = new Site { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Site Beta", CreatedAt = now, UpdatedAt = now };
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            Hostname = "host-01",
            Status = AgentStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };
        var namedAgent = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            Hostname = "host-02",
            DisplayName = "Servidor de Arquivos",
            Status = AgentStatus.Online,
            CreatedAt = now,
            UpdatedAt = now
        };

        var clientSensor = NewDefinition("Uso de disco", "disco_c", SensorType.DiskSpace, AppApprovalScopeType.Client, clientId: client.Id);
        var siteSensor = NewDefinition("Uso de CPU", "cpu_site", SensorType.Cpu, AppApprovalScopeType.Site, siteId: site.Id);
        var agentSensor = NewDefinition("Ping do host", "ping_agent", SensorType.Ping, AppApprovalScopeType.Agent, agentId: agent.Id);
        var namedAgentSensor = NewDefinition("Memória do host", "mem_agent", SensorType.Memory, AppApprovalScopeType.Agent, agentId: namedAgent.Id);
        var inactiveSensor = NewDefinition("Sensor desligado", "off_sensor", SensorType.Uptime, AppApprovalScopeType.Client, clientId: client.Id);
        inactiveSensor.IsActive = false;

        db.Clients.Add(client);
        db.Sites.Add(site);
        db.Agents.AddRange(agent, namedAgent);
        db.SensorDefinitions.AddRange(clientSensor, siteSensor, agentSensor, namedAgentSensor, inactiveSensor);
        await db.SaveChangesAsync();

        var service = new SensorDefinitionService(
            db,
            new TestAgentLabelRepository(),
            new TestAgentRepository(db),
            new TestSiteRepository(db));

        return new SensorFixture(db, service, clientSensor, siteSensor, agentSensor, namedAgentSensor, inactiveSensor);
    }

    private static SensorDefinition NewDefinition(
        string name,
        string key,
        SensorType sensorType,
        AppApprovalScopeType scopeType,
        Guid? clientId = null,
        Guid? siteId = null,
        Guid? agentId = null)
    {
        var now = DateTime.UtcNow;
        return new SensorDefinition
        {
            Id = Guid.NewGuid(),
            Key = key,
            Name = name,
            SensorType = sensorType,
            ScopeType = scopeType,
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            LastUpdatedAt = now
        };
    }

    private sealed class SensorFixture : IAsyncDisposable
    {
        public SensorFixture(
            DiscoveryDbContext db,
            SensorDefinitionService service,
            SensorDefinition clientSensor,
            SensorDefinition siteSensor,
            SensorDefinition agentSensor,
            SensorDefinition namedAgentSensor,
            SensorDefinition inactiveSensor)
        {
            Db = db;
            Service = service;
            ClientSensor = clientSensor;
            SiteSensor = siteSensor;
            AgentSensor = agentSensor;
            NamedAgentSensor = namedAgentSensor;
            InactiveSensor = inactiveSensor;
        }

        public DiscoveryDbContext Db { get; }
        public SensorDefinitionService Service { get; }
        public SensorDefinition ClientSensor { get; }
        public SensorDefinition SiteSensor { get; }
        public SensorDefinition AgentSensor { get; }
        public SensorDefinition NamedAgentSensor { get; }
        public SensorDefinition InactiveSensor { get; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
        }
    }

    private sealed class TestDiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(SensorDefinition)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
            });

            modelBuilder.Entity<Site>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
            });

            modelBuilder.Entity<Agent>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Hostname).IsRequired();
            });

            modelBuilder.Entity<SensorDefinition>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
            });
        }
    }

    private sealed class TestAgentRepository(DiscoveryDbContext db) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id)
            => db.Agents.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);

        public async Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        {
            var idList = ids.ToList();
            return await db.Agents.AsNoTracking()
                .Where(item => idList.Contains(item.Id))
                .ToListAsync(ct);
        }

        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotSupportedException();
        public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => throw new NotSupportedException();
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TestSiteRepository(DiscoveryDbContext db) : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id)
            => db.Sites.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);

        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) => throw new NotSupportedException();

        public async Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false)
        {
            var ids = siteIds.ToArray();
            var query = db.Sites.AsNoTracking().Where(item => ids.Contains(item.Id));
            if (!includeInactive)
                query = query.Where(item => item.IsActive);

            return await query.ToListAsync();
        }

        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class TestAgentLabelRepository : IAgentLabelRepository
    {
        public Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId) => Task.FromResult<IReadOnlyList<AgentLabel>>([]);
        public Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds) => Task.FromResult<IReadOnlyList<AgentLabel>>([]);
        public Task<(int Total, IReadOnlyList<AgentLabelRuleAgentResponse> Agents)> GetAgentsByRuleIdPagedAsync(Guid ruleId, int page, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetAgentIdsByLabelPagedAsync(string label, Guid? afterAgentId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CountAgentsByLabelAsync(string label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentLabelUsageDto>> GetLabelUsageAsync(int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDistinctLabelsAsync(int limit, AgentLabelSourceType? sourceType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentLabel?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task SuppressAutomaticLabelAsync(Guid agentId, string label, string? suppressedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearSuppressionAsync(Guid agentId, string label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentLabelSuppressionDto>> GetSuppressionsByAgentIdAsync(Guid agentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Guid?> ReleaseSuppressionAsync(Guid suppressionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentLabelChangeLogDto>> GetChangeLogAsync(Guid agentId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentLabelProtectedLabel>> GetProtectedLabelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentLabelProtectedLabel> AddProtectedLabelAsync(AgentLabelProtectedLabel protectedLabel, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveProtectedLabelAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentLabel> AddAsync(AgentLabel label) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }
}
