using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Testes de integracao do motor de auto-labeling contra um DbContext InMemory real.
///
/// Diferente dos testes com stubs, estes exercitam o caminho completo
/// (avaliacao -> matches -> labels efetivas) e cobrem regressoes que so aparecem
/// quando o estado e persistido, como o caso em que uma label deveria ser removida
/// na MESMA execucao em que o agente deixa de casar com a regra.
/// </summary>
public class AgentAutoLabelingServiceTests
{
    [Test]
    public async Task Evaluate_WhenRuleMatches_AddsAutomaticLabelAndMatch()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "test");

        var labels = await fx.Db.AgentLabels.AsNoTracking().ToListAsync();
        Assert.That(labels, Has.Count.EqualTo(1));
        Assert.That(labels[0].Label, Is.EqualTo("PROD"));
        Assert.That(labels[0].SourceType, Is.EqualTo(AgentLabelSourceType.Automatic));

        var matches = await fx.Db.AgentLabelRuleMatches.AsNoTracking().ToListAsync();
        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Evaluate_WhenAgentStopsMatching_RemovesLabelInSameRun()
    {
        // Regressao: a derivacao das labels passou a usar o dicionario em memoria de
        // matches. Remover de _db nao remove do dicionario, entao a label sobrevivia
        // a execucao e so saia na reconciliacao seguinte.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1), "pre-condicao: label aplicada");

        // O agente deixa de casar com a regra.
        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "second");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0),
            "A label deve ser removida na MESMA execucao em que o match deixa de existir.");
        Assert.That(await fx.Db.AgentLabelRuleMatches.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Evaluate_WhenRuleDisabled_RemovesLabel()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1));

        var rule = await fx.Db.AgentLabelRules.SingleAsync();
        rule.IsEnabled = false;
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "after-disable");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0),
            "Desabilitar a regra deve remover a label automatica.");
    }

    [Test]
    public async Task Evaluate_WhenApplyOnlyMode_KeepsLabelAfterAgentStopsMatching()
    {
        // Semantica de ApplyOnly: nao remover. Documenta explicitamente o comportamento.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyOnly);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");

        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "second");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "ApplyOnly preserva a label mesmo quando o agente deixa de casar.");
    }

    [Test]
    public async Task Evaluate_UpdatesLastEvaluatedAt_EvenWhenLabelUnchanged()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var first = (await fx.Db.AgentLabelRuleMatches.AsNoTracking().SingleAsync()).LastEvaluatedAt;

        await Task.Delay(15);
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "second");
        var second = (await fx.Db.AgentLabelRuleMatches.AsNoTracking().SingleAsync()).LastEvaluatedAt;

        Assert.That(second, Is.GreaterThan(first),
            "LastEvaluatedAt deve refletir toda avaliacao bem-sucedida, nao so quando a label muda.");
    }

    [Test]
    public async Task Evaluate_RecordsAuditTrail()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var logs = await fx.Db.AgentLabelChangeLogs.AsNoTracking().ToListAsync();

        Assert.That(logs, Has.Count.EqualTo(1));
        Assert.That(logs[0].Action, Is.EqualTo("Added"));
        Assert.That(logs[0].Label, Is.EqualTo("PROD"));
        Assert.That(logs[0].Reason, Is.EqualTo("first"));

        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "second");
        logs = await fx.Db.AgentLabelChangeLogs.AsNoTracking().OrderBy(l => l.OccurredAt).ToListAsync();

        Assert.That(logs, Has.Count.EqualTo(2));
        Assert.That(logs[1].Action, Is.EqualTo("Removed"));
    }

    [Test]
    public async Task ReprocessAllAgents_AppliesRuleToEveryAgent()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        // Um segundo agente que nao casa com a regra.
        fx.Db.Agents.Add(new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = fx.SiteId,
            Hostname = "WORKSTATION-77",
            Status = AgentStatus.Online,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fx.Db.SaveChangesAsync();

        await fx.Service.ReprocessAllAgentsAsync("bulk");

        var labels = await fx.Db.AgentLabels.AsNoTracking().ToListAsync();
        Assert.That(labels, Has.Count.EqualTo(1), "Apenas o agente com match deve receber a label.");
        Assert.That(labels[0].AgentId, Is.EqualTo(fx.AgentId));
    }

    [Test]
    public async Task EvaluateImpact_EstimatesMatchRateFromSample()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        fx.Db.Agents.Add(new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = fx.SiteId,
            Hostname = "WORKSTATION-77",
            Status = AgentStatus.Online,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fx.Db.SaveChangesAsync();

        var impact = await fx.Service.EvaluateImpactAsync(new Core.DTOs.AgentLabelRuleImpactRequest
        {
            Label = "PROD",
            ApplyMode = AgentLabelApplyMode.ApplyAndRemove,
            Expression = Fixture.HostnameContainsSrvExpression(),
            SampleSize = 100
        });

        Assert.That(impact.Sampled, Is.EqualTo(2));
        Assert.That(impact.Matched, Is.EqualTo(1));
        Assert.That(impact.EstimatedTotalAgents, Is.EqualTo(2));
        Assert.That(impact.WouldAddLabel, Is.EqualTo(1));
    }

    // -------------------------------------------------------------------------
    // Fixture
    // -------------------------------------------------------------------------

    private sealed class Fixture : IAsyncDisposable
    {
        public required DiscoveryDbContext Db { get; init; }
        public required AgentAutoLabelingService Service { get; init; }
        public required Guid AgentId { get; init; }
        public required Guid SiteId { get; init; }
        public required Guid RuleId { get; init; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public static Core.DTOs.AgentLabelRuleExpressionNodeDto HostnameContainsSrvExpression() => new()
        {
            NodeType = AgentLabelNodeType.Group,
            LogicalOperator = AgentLabelLogicalOperator.And,
            Children =
            [
                new Core.DTOs.AgentLabelRuleExpressionNodeDto
                {
                    NodeType = AgentLabelNodeType.Condition,
                    Field = AgentLabelField.Hostname,
                    Operator = AgentLabelComparisonOperator.StartsWith,
                    Value = "SRV"
                }
            ]
        };

        public static async Task<Fixture> CreateAsync(string hostname, AgentLabelApplyMode applyMode)
        {
            var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
                .UseInMemoryDatabase($"auto-labeling-tests-{Guid.NewGuid():N}")
                .Options;

            var db = new LabelTestDbContext(options);

            var now = DateTime.UtcNow;
            var client = new Client { Id = Guid.NewGuid(), Name = "Client 01", CreatedAt = now, UpdatedAt = now };
            var site = new Site { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Site 01", CreatedAt = now, UpdatedAt = now };
            var agent = new Agent
            {
                Id = Guid.NewGuid(),
                SiteId = site.Id,
                Hostname = hostname,
                Status = AgentStatus.Online,
                CreatedAt = now,
                UpdatedAt = now
            };

            var rule = new AgentLabelRule
            {
                Id = Guid.NewGuid(),
                Name = "Servidores",
                Label = "PROD",
                IsEnabled = true,
                ApplyMode = applyMode,
                ExpressionJson = System.Text.Json.JsonSerializer.Serialize(
                    HostnameContainsSrvExpression(),
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Clients.Add(client);
            db.Sites.Add(site);
            db.Agents.Add(agent);
            db.AgentLabelRules.Add(rule);
            await db.SaveChangesAsync();

            var service = new AgentAutoLabelingService(
                db,
                new AgentRepository(db),
                new AgentHardwareRepository(db),
                new AgentSoftwareRepository(db),
                new AgentLabelRuleRepository(db),
                new SiteRepository(db),
                new NoopRedisService(),
                NullLogger<AgentAutoLabelingService>.Instance);

            return new Fixture
            {
                Db = db,
                Service = service,
                AgentId = agent.Id,
                SiteId = site.Id,
                RuleId = rule.Id
            };
        }
    }

    /// <summary>
    /// Contexto restrito as entidades do auto-labeling. O DiscoveryDbContext completo
    /// mapeia dezenas de entidades cujas configuracoes dependem de recursos que o
    /// provider InMemory nao suporta, entao isolamos apenas o necessario.
    /// </summary>
    private sealed class LabelTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(AgentLabelRule),
                typeof(AgentLabel),
                typeof(AgentLabelRuleMatch),
                typeof(AgentLabelChangeLog),
                typeof(CustomFieldDefinition),
                typeof(CustomFieldValue)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Agent>(entity => entity.HasKey(item => item.Id));

            modelBuilder.Entity<AgentLabelRule>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.ExpressionJson).IsRequired();
            });

            modelBuilder.Entity<AgentLabel>(entity => entity.HasKey(item => item.Id));

            modelBuilder.Entity<AgentLabelRuleMatch>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.HasIndex(item => new { item.RuleId, item.AgentId }).IsUnique();
            });

            modelBuilder.Entity<AgentLabelChangeLog>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<CustomFieldDefinition>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<CustomFieldValue>(entity => entity.HasKey(item => item.Id));
        }
    }

    private sealed class NoopRedisService : IRedisService
    {
        private readonly Dictionary<string, string> _store = [];

        public bool IsConnected => true;
        public Task<string?> GetAsync(string key) => Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(true);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(-1);
        public Task DeleteAsync(string key)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }
}
