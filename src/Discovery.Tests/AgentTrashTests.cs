using System.Reflection;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Cqrs.Agents.Crud.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Lixeira de agentes: listagem de soft-deleted, restauração e exclusão
/// definitiva (purge). O SQL do <see cref="IAgentPurgeService"/> é provider
/// específico e não roda no InMemory — por isso o serviço é mockado aqui; a
/// cobertura da lista de tabelas fica a cargo da revisão do serviço.
/// </summary>
public class AgentTrashTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static DiscoveryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"agent-trash-{Guid.NewGuid():N}")
            .Options;
        return new AgentTrashTestDbContext(options);
    }

    private static Agent NewAgent(string hostname, DateTime? deletedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = SiteId,
        Hostname = hostname,
        DisplayName = hostname,
        Status = AgentStatus.Offline,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        DeletedAt = deletedAt
    };

    private static async Task SeedBaseAsync(DiscoveryDbContext db)
    {
        db.Clients.Add(new Client { Id = ClientId, Name = "Cliente", IsActive = true });
        db.Sites.Add(new Site { Id = SiteId, ClientId = ClientId, Name = "Site", IsActive = true });
        await db.SaveChangesAsync();
    }

    private static Ticket NewTicket(Guid agentId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        SiteId = SiteId,
        AgentId = agentId,
        Title = "Chamado",
        Description = "Descrição",
        WorkflowStateId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    // -------------------------------------------------------------------------
    // Listagem da lixeira
    // -------------------------------------------------------------------------

    [Test]
    public async Task GetDeleted_ReturnsOnlySoftDeleted_WithClientAndDeletedAt()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);

        var active = NewAgent("ATIVO");
        var deletedOld = NewAgent("EXCLUIDO-ANTIGO", DateTime.UtcNow.AddDays(-2));
        var deletedNew = NewAgent("EXCLUIDO-NOVO", DateTime.UtcNow);
        db.Agents.AddRange(active, deletedOld, deletedNew);
        await db.SaveChangesAsync();

        var handler = new GetDeletedAgentsQueryHandler(db, new FakeSiteRepository(db));
        var result = await handler.Handle(new GetDeletedAgentsQuery(null, null, 1, 50), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Total, Is.EqualTo(2));
        Assert.That(result.Value.Items.Select(item => item.Hostname), Is.EquivalentTo(new[] { "EXCLUIDO-NOVO", "EXCLUIDO-ANTIGO" }));
        Assert.That(result.Value.Items.All(item => item.DeletedAt is not null), Is.True);
        Assert.That(result.Value.Items.All(item => item.ClientId == ClientId), Is.True);
        Assert.That(result.Value.Items.All(item => !item.IsOnline && item.Status == "Offline"), Is.True);
        // Mais recente primeiro (ordenação por DeletedAt desc).
        Assert.That(result.Value.Items[0].Hostname, Is.EqualTo("EXCLUIDO-NOVO"));
    }

    [Test]
    public async Task GetDeleted_FiltersByClient()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);

        var otherClientId = Guid.NewGuid();
        var otherSiteId = Guid.NewGuid();
        db.Clients.Add(new Client { Id = otherClientId, Name = "Outro", IsActive = true });
        db.Sites.Add(new Site { Id = otherSiteId, ClientId = otherClientId, Name = "Outro site", IsActive = true });
        db.Agents.AddRange(
            NewAgent("DO-CLIENTE", DateTime.UtcNow),
            new Agent { Id = Guid.NewGuid(), SiteId = otherSiteId, Hostname = "DE-OUTRO", Status = AgentStatus.Offline, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, DeletedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var handler = new GetDeletedAgentsQueryHandler(db, new FakeSiteRepository(db));
        var result = await handler.Handle(new GetDeletedAgentsQuery(ClientId, null, 1, 50), CancellationToken.None);

        Assert.That(result.Value!.Total, Is.EqualTo(1));
        Assert.That(result.Value.Items[0].Hostname, Is.EqualTo("DO-CLIENTE"));
    }

    // -------------------------------------------------------------------------
    // Restauração
    // -------------------------------------------------------------------------

    [Test]
    public async Task Restore_ClearsDeletedAt()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);
        var agent = NewAgent("EXCLUIDO", DateTime.UtcNow);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var handler = new RestoreAgentCommandHandler(db, new NoopRedisService(), new FakeSiteRepository(db));
        var result = await handler.Handle(new RestoreAgentCommand(agent.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var restored = await db.Agents.IgnoreQueryFilters().SingleAsync(item => item.Id == agent.Id);
        Assert.That(restored.DeletedAt, Is.Null);
        // Volta a aparecer na consulta normal (filtro global).
        Assert.That(await db.Agents.AnyAsync(item => item.Id == agent.Id), Is.True);
    }

    [Test]
    public async Task Restore_IsIdempotent_ForActiveAgent()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);
        var agent = NewAgent("ATIVO");
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var handler = new RestoreAgentCommandHandler(db, new NoopRedisService(), new FakeSiteRepository(db));
        var result = await handler.Handle(new RestoreAgentCommand(agent.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That((await db.Agents.SingleAsync(item => item.Id == agent.Id)).DeletedAt, Is.Null);
    }

    [Test]
    public async Task Restore_NotFound_ForUnknownAgent()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);

        var handler = new RestoreAgentCommandHandler(db, new NoopRedisService(), new FakeSiteRepository(db));
        var result = await handler.Handle(new RestoreAgentCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    // -------------------------------------------------------------------------
    // Exclusão definitiva
    // -------------------------------------------------------------------------

    [Test]
    public async Task Purge_ActiveAgent_IsRejected()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);
        var agent = NewAgent("ATIVO");
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var purge = new FakePurgeService();
        var handler = new PurgeAgentCommandHandler(db, purge, new NoopRedisService(), new FakeSiteRepository(db), NullLogger<PurgeAgentCommandHandler>.Instance);
        var result = await handler.Handle(new PurgeAgentCommand(agent.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Conflict"));
        Assert.That(purge.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task Purge_WithLinkedTickets_RequiresForce()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);
        var agent = NewAgent("EXCLUIDO", DateTime.UtcNow);
        db.Agents.Add(agent);
        db.Tickets.Add(NewTicket(agent.Id));
        await db.SaveChangesAsync();

        var purge = new FakePurgeService();
        var handler = new PurgeAgentCommandHandler(db, purge, new NoopRedisService(), new FakeSiteRepository(db), NullLogger<PurgeAgentCommandHandler>.Instance);

        var withoutForce = await handler.Handle(new PurgeAgentCommand(agent.Id), CancellationToken.None);
        Assert.That(withoutForce.IsSuccess, Is.False);
        Assert.That(withoutForce.Errors[0].Code, Is.EqualTo("Conflict"));
        Assert.That(withoutForce.Errors[0].Message, Does.Contain("1 chamado"));
        Assert.That(purge.Calls, Is.EqualTo(0));

        var withForce = await handler.Handle(new PurgeAgentCommand(agent.Id, Force: true), CancellationToken.None);
        Assert.That(withForce.IsSuccess, Is.True);
        Assert.That(purge.Calls, Is.EqualTo(1));
        Assert.That(purge.LastAgentId, Is.EqualTo(agent.Id));
    }

    [Test]
    public async Task Purge_WithoutTickets_RunsService()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);
        var agent = NewAgent("EXCLUIDO", DateTime.UtcNow);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var purge = new FakePurgeService();
        var handler = new PurgeAgentCommandHandler(db, purge, new NoopRedisService(), new FakeSiteRepository(db), NullLogger<PurgeAgentCommandHandler>.Instance);
        var result = await handler.Handle(new PurgeAgentCommand(agent.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(purge.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Purge_NotFound_ForUnknownAgent()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);

        var purge = new FakePurgeService();
        var handler = new PurgeAgentCommandHandler(db, purge, new NoopRedisService(), new FakeSiteRepository(db), NullLogger<PurgeAgentCommandHandler>.Instance);
        var result = await handler.Handle(new PurgeAgentCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
        Assert.That(purge.Calls, Is.EqualTo(0));
    }

    // -------------------------------------------------------------------------
    // Labels não contam agentes na lixeira
    // -------------------------------------------------------------------------

    [Test]
    public async Task LabelUsage_And_Ids_ExcludeSoftDeletedAgents()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);

        var active = NewAgent("ATIVO");
        var deleted = NewAgent("EXCLUIDO", DateTime.UtcNow);
        db.Agents.AddRange(active, deleted);
        db.AgentLabels.AddRange(
            new AgentLabel { Id = Guid.NewGuid(), AgentId = active.Id, Label = "Windows", SourceType = AgentLabelSourceType.Automatic },
            new AgentLabel { Id = Guid.NewGuid(), AgentId = deleted.Id, Label = "Windows", SourceType = AgentLabelSourceType.Automatic });
        await db.SaveChangesAsync();

        var repo = new AgentLabelRepository(db);

        var usage = await repo.GetLabelUsageAsync(200);
        var windows = usage.Single(item => item.Label == "Windows");
        Assert.That(windows.AgentCount, Is.EqualTo(1));

        Assert.That(await repo.CountAgentsByLabelAsync("Windows"), Is.EqualTo(1));

        var ids = await repo.GetAgentIdsByLabelPagedAsync("Windows", null, 500);
        Assert.That(ids, Is.EquivalentTo(new[] { active.Id }));
    }

    /// <summary>
    /// Garante que TODA entidade mapeada com propriedade AgentId está coberta
    /// pelo AgentPurgeService (delete ou desvínculo). Tabela nova referenciando
    /// agents sem entrar na lista quebraria o hard delete em runtime.
    /// </summary>
    [Test]
    public void PurgeTableLists_CoverEveryMappedAgentReference()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=discovery_model;Username=model;Password=model",
                npgsql => npgsql.UseVector())
            .Options;

        using var db = new DiscoveryDbContext(options);

        var fields = typeof(AgentPurgeService)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static);
        var owned = (string[])fields.Single(f => f.Name == "AgentOwnedTables").GetValue(null)!;
        var detached = (string[])fields.Single(f => f.Name == "DetachedTables").GetValue(null)!;
        var covered = owned.Concat(detached).ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var entityType in db.Model.GetEntityTypes())
        {
            // Cobre AgentId por nome OU por coluna agent_id (shadow property).
            var referencesAgent = entityType.GetProperties().Any(property =>
                string.Equals(property.Name, "AgentId", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property.GetColumnName(), "agent_id", StringComparison.OrdinalIgnoreCase));
            if (!referencesAgent)
                continue;

            var table = entityType.GetTableName();
            if (string.IsNullOrWhiteSpace(table) || covered.Contains(table))
                continue;

            missing.Add(entityType.DisplayName() + " -> " + table);
        }

        Assert.That(
            missing,
            Is.Empty,
            "Tabelas com AgentId fora do AgentPurgeService: " + string.Join(", ", missing));
    }

    [Test]
    public async Task GetDeleted_NonGlobalAccess_OnlyAllowedClients()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);

        var otherClientId = Guid.NewGuid();
        var otherSiteId = Guid.NewGuid();
        db.Clients.Add(new Client { Id = otherClientId, Name = "Outro", IsActive = true });
        db.Sites.Add(new Site { Id = otherSiteId, ClientId = otherClientId, Name = "Outro site", IsActive = true });
        db.Agents.AddRange(
            NewAgent("DO-CLIENTE", DateTime.UtcNow),
            new Agent { Id = Guid.NewGuid(), SiteId = otherSiteId, Hostname = "DE-OUTRO", Status = AgentStatus.Offline, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, DeletedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var handler = new GetDeletedAgentsQueryHandler(db, new FakeSiteRepository(db));

        var scoped = await handler.Handle(
            new GetDeletedAgentsQuery(null, null, 1, 50, HasGlobalAccess: false, AllowedClientIds: [ClientId]),
            CancellationToken.None);
        Assert.That(scoped.Value!.Total, Is.EqualTo(1));
        Assert.That(scoped.Value.Items[0].Hostname, Is.EqualTo("DO-CLIENTE"));

        var none = await handler.Handle(
            new GetDeletedAgentsQuery(null, null, 1, 50, HasGlobalAccess: false, AllowedClientIds: []),
            CancellationToken.None);
        Assert.That(none.Value!.Total, Is.EqualTo(0));
    }

    [Test]
    public async Task GetDeleted_Search_FiltersByHostnameOrIp()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db);

        db.Agents.AddRange(
            NewAgent("ALFA-01", DateTime.UtcNow),
            NewAgent("BETA-02", DateTime.UtcNow));
        await db.SaveChangesAsync();

        var handler = new GetDeletedAgentsQueryHandler(db, new FakeSiteRepository(db));
        var result = await handler.Handle(new GetDeletedAgentsQuery(null, null, 1, 50, Search: "beta"), CancellationToken.None);

        Assert.That(result.Value!.Total, Is.EqualTo(1));
        Assert.That(result.Value.Items[0].Hostname, Is.EqualTo("BETA-02"));
    }

    [Test]
    public async Task GetDeleted_Search_MatchesClientName()
    {
        using var db = CreateDbContext();
        await SeedBaseAsync(db); // cliente "Cliente", site "Site"
        db.Agents.Add(NewAgent("PC-01", DateTime.UtcNow));
        await db.SaveChangesAsync();

        var handler = new GetDeletedAgentsQueryHandler(db, new FakeSiteRepository(db));
        var result = await handler.Handle(
            new GetDeletedAgentsQuery(null, null, 1, 50, Search: "cliente"),
            CancellationToken.None);

        Assert.That(result.Value!.Total, Is.EqualTo(1));
        Assert.That(result.Value.Items[0].Hostname, Is.EqualTo("PC-01"));
    }

    // -------------------------------------------------------------------------
    // Fakes / contexto de teste
    // -------------------------------------------------------------------------

    private sealed class FakePurgeService : IAgentPurgeService
    {
        public int Calls { get; private set; }
        public Guid? LastAgentId { get; private set; }

        public Task PurgeAsync(Guid agentId, CancellationToken ct = default)
        {
            Calls++;
            LastAgentId = agentId;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSiteRepository(DiscoveryDbContext db) : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id)
            => db.Sites.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);

        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>(db.Sites.Where(item => item.ClientId == clientId).ToList());

        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false)
        {
            var ids = clientIds.ToHashSet();
            return Task.FromResult<IEnumerable<Site>>(db.Sites.Where(item => ids.Contains(item.ClientId)).ToList());
        }

        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>(db.Sites.ToList());

        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false)
        {
            var ids = siteIds.ToHashSet();
            return Task.FromResult<IEnumerable<Site>>(db.Sites.Where(item => ids.Contains(item.Id)).ToList());
        }

        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class NoopRedisService : IRedisService
    {
        public bool IsConnected => true;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.CompletedTask;
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(true);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(-1);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }

    /// <summary>
    /// Contexto reduzido: só as entidades usadas aqui, mas com o MESMO filtro
    /// global de soft delete do agente que existe em produção.
    /// </summary>
    private sealed class AgentTrashTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(Ticket),
                typeof(AgentLabel)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Agent>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.HasQueryFilter(agent => agent.DeletedAt == null);
            });
            modelBuilder.Entity<Ticket>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentLabel>(entity => entity.HasKey(item => item.Id));
        }
    }
}
