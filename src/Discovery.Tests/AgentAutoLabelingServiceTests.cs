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

    [Test]
    public async Task Evaluate_WhenManualLabelHasSameName_DoesNotViolateUniqueIndex()
    {
        // Regressao: o indice unico e (agent_id, label) ignorando a origem. Uma label
        // manual "PROD" + uma regra que gera "PROD" fazia o SaveChanges do lote inteiro
        // falhar com DbUpdateException.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        fx.Db.AgentLabels.Add(new AgentLabel
        {
            Id = Guid.NewGuid(),
            AgentId = fx.AgentId,
            Label = "PROD",
            SourceType = AgentLabelSourceType.Manual,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fx.Db.SaveChangesAsync();

        Assert.DoesNotThrowAsync(() => fx.Service.EvaluateAgentAsync(fx.AgentId, "collision"));

        var labels = await fx.Db.AgentLabels.AsNoTracking().ToListAsync();
        Assert.That(labels, Has.Count.EqualTo(1), "Nao deve duplicar a label ja existente.");
        Assert.That(labels[0].SourceType, Is.EqualTo(AgentLabelSourceType.Manual));
    }

    [Test]
    public async Task Evaluate_WhenAutomaticLabelManuallyRemoved_DoesNotRecreateIt()
    {
        // Sem a supressao, o reconcile recriava a label logo apos o usuario remove-la.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var label = await fx.Db.AgentLabels.SingleAsync();
        Assert.That(label.Label, Is.EqualTo("PROD"));

        // Usuario remove manualmente a label automatica (com supressao).
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "after-manual-removal");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0),
            "A label removida manualmente nao deve ser recriada pelo reconcile.");
    }

    [Test]
    public async Task Evaluate_WhenRuleStopsMatching_ClearsSuppressionSoFutureMatchReapplies()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var label = await fx.Db.AgentLabels.SingleAsync();
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);

        // Agente deixa de casar com a regra.
        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "no-match");

        Assert.That(await fx.Db.AgentLabelSuppressions.CountAsync(), Is.EqualTo(0),
            "A supressao deve ser limpa quando a regra deixa de casar.");

        // Agora volta a casar: a label deve ser reaplicada.
        agent.Hostname = "SRV-PROD-02";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "match-again");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "Um novo match deve reaplicar a label.");
    }

    [Test]
    public async Task EvaluateImpact_SampleIsRepresentativeAcrossSites()
    {
        // Amostragem estratificada: um cliente grande nao deve dominar a amostra.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        var now = DateTime.UtcNow;
        // 30 agentes "SRV" no site A (o original) + 30 "WORKSTATION" em um site novo.
        var otherSite = Guid.NewGuid();
        for (var i = 0; i < 30; i++)
        {
            fx.Db.Agents.Add(new Agent
            {
                Id = Guid.NewGuid(),
                SiteId = fx.SiteId,
                Hostname = "SRV-" + i,
                Status = AgentStatus.Online,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        for (var i = 0; i < 30; i++)
        {
            fx.Db.Agents.Add(new Agent
            {
                Id = Guid.NewGuid(),
                SiteId = otherSite,
                Hostname = "WORKSTATION-" + i,
                Status = AgentStatus.Online,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        await fx.Db.SaveChangesAsync();

        var impact = await fx.Service.EvaluateImpactAsync(new Core.DTOs.AgentLabelRuleImpactRequest
        {
            Label = "PROD",
            ApplyMode = AgentLabelApplyMode.ApplyAndRemove,
            Expression = Fixture.HostnameContainsSrvExpression(),
            SampleSize = 20
        });

        Assert.That(impact.EstimatedTotalAgents, Is.EqualTo(61));
        Assert.That(impact.Sampled, Is.LessThanOrEqualTo(20));

        // Com amostragem estratificada, ambos os estratos entram na amostra, entao o
        // match nao pode ser 0 nem a amostra inteira (que era o vies do "primeiros N").
        Assert.That(impact.Matched, Is.GreaterThan(0), "O estrato com match deve aparecer na amostra.");
        Assert.That(impact.Matched, Is.LessThan(impact.Sampled), "O estrato sem match tambem deve aparecer.");
    }
    [Test]
    public async Task Evaluate_WhenLabelReaddedManually_ClearsStaleSuppression()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var label = await fx.Db.AgentLabels.SingleAsync();
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);
        Assert.That(await fx.Db.AgentLabelSuppressions.CountAsync(), Is.EqualTo(1));

        // Usuario readiciona a label na mao.
        await fx.LabelRepository.ClearSuppressionAsync(fx.AgentId, "PROD");
        await fx.LabelRepository.AddAsync(new AgentLabel
        {
            Id = Guid.NewGuid(),
            AgentId = fx.AgentId,
            Label = "PROD",
            SourceType = AgentLabelSourceType.Manual,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        Assert.That(await fx.Db.AgentLabelSuppressions.CountAsync(), Is.EqualTo(0),
            "Readicionar a label deve limpar a supressao orfa.");
    }
    [Test]
    public async Task Evaluate_DiskFullScenario_LabelReappliesWhenConditionReturns_ApplyAndRemove()
    {
        // Cenario do usuario: "HD cheio" aplica a label, o usuario remove, o HD e
        // liberado e depois enche DE NOVO -> a label deve voltar.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-full-1");
        var label = await fx.Db.AgentLabels.SingleAsync();
        Assert.That(label.Label, Is.EqualTo("PROD"), "1) condicao verdadeira aplica a label");

        // Usuario remove manualmente (supressao).
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);

        // HD liberado: a condicao deixa de ser verdadeira.
        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-freed");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0), "2) sem match, sem label");
        Assert.That(await fx.Db.AgentLabelSuppressions.CountAsync(), Is.EqualTo(0),
            "3) a supressao deve ser liberada quando a condicao deixa de valer");

        // HD enche NOVAMENTE.
        agent.Hostname = "SRV-PROD-02";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-full-2");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "4) a label deve voltar a ser aplicada quando a condicao retorna");
    }
    [Test]
    public async Task Evaluate_DiskFullScenario_ApplyOnly_LabelReappliesWhenConditionReturns()
    {
        // Em ApplyOnly o match NUNCA e removido. Se a supressao so fosse liberada
        // quando a condicao deixa de valer, a label nunca voltaria — contrariando
        // o cenario do usuario.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyOnly);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-full-1");
        var label = await fx.Db.AgentLabels.SingleAsync();

        // Usuario remove manualmente.
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);

        // HD liberado.
        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-freed");

        // HD enche novamente.
        agent.Hostname = "SRV-PROD-02";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-full-2");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "ApplyOnly: a label deve voltar quando a condicao retorna");
    }
    [Test]
    public async Task Suppressions_AreListedAndReleasable()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var label = await fx.Db.AgentLabels.SingleAsync();
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);

        // A supressao fica visivel, com o nome da regra que produz a label.
        var listed = await fx.LabelRepository.GetSuppressionsByAgentIdAsync(fx.AgentId);
        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.That(listed[0].Label, Is.EqualTo("PROD"));
        Assert.That(listed[0].SuppressedBy, Is.EqualTo("tester"));
        Assert.That(listed[0].RuleName, Is.EqualTo("Servidores"));

        // Liberar a supressao faz a label voltar na proxima avaliacao.
        var released = await fx.LabelRepository.ReleaseSuppressionAsync(listed[0].Id);
        Assert.That(released, Is.True);
        Assert.That(await fx.Db.AgentLabelSuppressions.CountAsync(), Is.EqualTo(0));

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "after-release");
        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "Liberar a supressao deve permitir a reaplicacao da label.");
    }
    // -------------------------------------------------------------------------
    // Fixture
    // -------------------------------------------------------------------------

    private sealed class Fixture : IAsyncDisposable
    {
        public required DiscoveryDbContext Db { get; init; }
        public required AgentAutoLabelingService Service { get; init; }
        public required AgentLabelRepository LabelRepository { get; init; }
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
                LabelRepository = new AgentLabelRepository(db),
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
                typeof(AgentLabelSuppression),
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

            modelBuilder.Entity<AgentLabelSuppression>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.HasIndex(item => new { item.AgentId, item.Label }).IsUnique();
            });
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
