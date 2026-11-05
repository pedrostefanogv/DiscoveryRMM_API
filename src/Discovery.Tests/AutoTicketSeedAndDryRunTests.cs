using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.AutoTicketRules.Commands;
using Discovery.Core.Cqrs.AutoTicketRules.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Cqrs.AutoTicketRules;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Cobre os endpoints que o frontend JÁ chamava mas que não existiam no backend
/// (auto-ticket-rules.ts: dryRun / seedDefaults / getStats): o "contrato morto".
/// </summary>
[TestFixture]
public class AutoTicketSeedAndDryRunTests
{
    // ── seed-defaults ──────────────────────────────────────────────────────

    [Test]
    public async Task SeedDefaults_ShouldCreateConservativeRuleSet_OnFirstRun()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var handler = new SeedDefaultAutoTicketRulesCommandHandler(repo, new AutoTicketRuleEngineService(repo, new MonitoringEventNormalizationService()));

        var result = await handler.Handle(
            new SeedDefaultAutoTicketRulesCommand(new SeedDefaultAutoTicketRulesRequest
            {
                ClientId = Guid.NewGuid(),
                IsEnabled = true
            }), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Value.CreatedCount, Is.GreaterThan(0));
            Assert.That(result.Value.UpdatedCount, Is.EqualTo(0));
            Assert.That(repo.Rules, Is.Not.Empty);
        });
    }

    [Test]
    public async Task SeedDefaults_ShouldBeIdempotent_OnSecondRun()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var handler = new SeedDefaultAutoTicketRulesCommandHandler(repo, new AutoTicketRuleEngineService(repo, new MonitoringEventNormalizationService()));
        var request = new SeedDefaultAutoTicketRulesRequest { ClientId = Guid.NewGuid(), IsEnabled = true };

        var first = await handler.Handle(new SeedDefaultAutoTicketRulesCommand(request), CancellationToken.None);
        var countAfterFirst = repo.Rules.Count;

        var second = await handler.Handle(new SeedDefaultAutoTicketRulesCommand(request), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second.Value.CreatedCount, Is.EqualTo(0), "Não pode duplicar.");
            Assert.That(second.Value.UpdatedCount, Is.EqualTo(first.Value.CreatedCount));
            Assert.That(repo.Rules, Has.Count.EqualTo(countAfterFirst), "A segunda execução não cria regras novas.");
        });
    }

    [Test]
    public async Task SeedDefaults_ShouldNotCreateTicketsForNoisySensors()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var handler = new SeedDefaultAutoTicketRulesCommandHandler(repo, new AutoTicketRuleEngineService(repo, new MonitoringEventNormalizationService()));

        await handler.Handle(
            new SeedDefaultAutoTicketRulesCommand(new SeedDefaultAutoTicketRulesRequest { ClientId = Guid.NewGuid() }),
            CancellationToken.None);

        // CPU e memória são ruidosos: devem nascer como AlertOnly, não CreateTicket.
        var cpu = repo.Rules.Single(r => r.AlertCodeFilter == "sensor:cpu");
        var memory = repo.Rules.Single(r => r.AlertCodeFilter == "sensor:memory");
        var disk = repo.Rules.Single(r => r.AlertCodeFilter == "sensor:disk_space");

        Assert.Multiple(() =>
        {
            Assert.That(cpu.Action, Is.EqualTo(AutoTicketRuleAction.AlertOnly));
            Assert.That(memory.Action, Is.EqualTo(AutoTicketRuleAction.AlertOnly));
            Assert.That(disk.Action, Is.EqualTo(AutoTicketRuleAction.CreateTicket),
                "Disco cheio é acionável e deve abrir chamado.");
        });
    }

    [Test]
    public async Task SeedDefaults_ShouldRejectEmptyClientId()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var handler = new SeedDefaultAutoTicketRulesCommandHandler(repo, new AutoTicketRuleEngineService(repo, new MonitoringEventNormalizationService()));

        var result = await handler.Handle(
            new SeedDefaultAutoTicketRulesCommand(new SeedDefaultAutoTicketRulesRequest { ClientId = Guid.Empty }),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
    }

    [Test]
    public async Task SeedDefaults_ShouldScopeRulesToTheClient()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var handler = new SeedDefaultAutoTicketRulesCommandHandler(repo, new AutoTicketRuleEngineService(repo, new MonitoringEventNormalizationService()));
        var clientId = Guid.NewGuid();

        await handler.Handle(
            new SeedDefaultAutoTicketRulesCommand(new SeedDefaultAutoTicketRulesRequest { ClientId = clientId }),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(repo.Rules, Is.All.Matches<AutoTicketRule>(r => r.ScopeLevel == AutoTicketScopeLevel.Client));
            Assert.That(repo.Rules, Is.All.Matches<AutoTicketRule>(r => r.ScopeId == clientId),
                "As regras semeadas pertencem a UM cliente — a herança cuida do resto.");
        });
    }

    // ── sensor key extraction (usada pelo dry-run) ─────────────────────────

    [TestCase("sensor:disk_space", "disk_space")]
    [TestCase("sensor:", null)]
    [TestCase("alert.code", null)]
    public void ExtractSensorKey_ShouldParse(string alertCode, string? expected)
    {
        Assert.That(AutoTicketRuleDryRunQueryHandler.ExtractSensorKey(alertCode), Is.EqualTo(expected));
    }

    // ── Dry-run: a simulação precisa refletir a política POR SENSOR ───────
    // Antes, o evento sintético não tinha SourceRefId, então o avaliador não
    // carregava a SensorDefinition e o "Never" do sensor era IGNORADO na
    // simulação — ela diria "criaria" onde a produção bloqueia.

    [Test]
    public async Task DryRun_ShouldNotCreateTicket_WhenSensorIsMarkedNever()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var sensorId = Guid.NewGuid();

        db.SensorDefinitions.Add(new SensorDefinition
        {
            Id = sensorId,
            Key = "disk_space",
            Name = "Disco",
            AutoTicketPolicy = SensorAutoTicketPolicy.Never
        });
        await db.SaveChangesAsync();

        // Precisa existir uma REGRA que case; senão o motor de regras
        // curto-circuita antes do avaliador de sinal e o teste não provaria nada
        // sobre a política do sensor.
        var ruleRepo = new InMemoryAutoTicketRuleRepository();
        ruleRepo.Rules.Add(new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "Disco abre chamado",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Client,
            ScopeId = clientId,
            AlertCodeFilter = "sensor:disk_space",
            Action = AutoTicketRuleAction.CreateTicket,
            DedupWindowMinutes = 60,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var handler = BuildDryRunHandler(db, ruleRepo);

        var result = await handler.Handle(
            new AutoTicketRuleDryRunQuery(null, new AutoTicketRuleDryRunRequest
            {
                ClientId = clientId,
                AgentId = agentId,
                AlertCode = "sensor:disk_space"
            }), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Value.WouldCreateTicket, Is.False,
                "A simulacao precisa respeitar o Never do sensor, igual a producao.");
            Assert.That(result.Value.Reason, Does.Contain("never").IgnoreCase,
                "O motivo deve citar que o SENSOR nunca abre chamado.");
        });
    }

    [Test]
    public async Task DryRun_ShouldResolveSensor_EvenWhenKeyHasNoDefinition()
    {
        await using var db = CreateDb();
        var handler = BuildDryRunHandler(db);

        // Sem definicao cadastrada: nao pode lancar, apenas seguir sem politica de sensor.
        var result = await handler.Handle(
            new AutoTicketRuleDryRunQuery(null, new AutoTicketRuleDryRunRequest
            {
                ClientId = Guid.NewGuid(),
                AgentId = Guid.NewGuid(),
                AlertCode = "sensor:inexistente"
            }), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task DryRun_ShouldRejectEmptyClientId()
    {
        await using var db = CreateDb();
        var handler = BuildDryRunHandler(db);

        var result = await handler.Handle(
            new AutoTicketRuleDryRunQuery(null, new AutoTicketRuleDryRunRequest
            {
                ClientId = Guid.Empty,
                AlertCode = "sensor:disk_space"
            }), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
    }

    [Test]
    public async Task DryRun_ShouldExplainCanaryVeto_ConsistentlyWithTheResult()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();

        var ruleRepo = new InMemoryAutoTicketRuleRepository();
        ruleRepo.Rules.Add(new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "Disco abre chamado",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Client,
            ScopeId = clientId,
            AlertCodeFilter = "sensor:disk_space",
            Action = AutoTicketRuleAction.CreateTicket,
            DedupWindowMinutes = 60,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        // Canario restrito a OUTRO cliente: este esta fora do escopo.
        var canaryPolicy = new ResolvedAutoTicketPolicy
        {
            Enabled = true,
            Mode = AutoTicketScopeMode.Canary,
            CanaryClientIds = [Guid.NewGuid().ToString()],
            SuppressWhenHostNotReachable = false,
            RespectAgentMaintenance = false,
            RespectMaintenanceWindow = false,
            MinConsecutiveFailures = 0,
            MinDurationSeconds = 0,
            FlapWindowMinutes = 0,
            FlapTransitionsThreshold = 0
        };

        var handler = BuildDryRunHandler(db, ruleRepo, canaryPolicy);

        var result = await handler.Handle(
            new AutoTicketRuleDryRunQuery(null, new AutoTicketRuleDryRunRequest
            {
                ClientId = clientId,
                AgentId = Guid.NewGuid(),
                AlertCode = "sensor:disk_space"
            }), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Value.WouldCreateTicket, Is.False);
            Assert.That(result.Value.Reason, Does.Contain("canary"),
                "O motivo precisa citar o canario. Sem isso a simulacao diz que criaria "
                + "enquanto o resultado diz que nao — contradicao que destroi a confianca.");
        });
    }

    /// <summary>
    /// Invariante: MOTIVO e RESULTADO nao podem se contradizer. Um veto sem
    /// explicacao e inutil para quem esta validando a configuracao.
    /// </summary>
    [TestCase(false, AutoTicketScopeMode.Off)]
    [TestCase(true, AutoTicketScopeMode.Shadow)]
    public async Task DryRun_ReasonMustNeverContradictTheResult(bool enabled, AutoTicketScopeMode mode)
    {
        await using var db = CreateDb();
        var policy = new ResolvedAutoTicketPolicy
        {
            Enabled = enabled,
            Mode = mode,
            SuppressWhenHostNotReachable = false,
            RespectAgentMaintenance = false,
            RespectMaintenanceWindow = false,
            MinConsecutiveFailures = 0,
            MinDurationSeconds = 0,
            FlapWindowMinutes = 0,
            FlapTransitionsThreshold = 0
        };

        var handler = BuildDryRunHandler(db, null, policy);

        var result = await handler.Handle(
            new AutoTicketRuleDryRunQuery(null, new AutoTicketRuleDryRunRequest
            {
                ClientId = Guid.NewGuid(),
                AgentId = Guid.NewGuid(),
                AlertCode = "sensor:disk_space"
            }), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Value.WouldCreateTicket, Is.False);
            Assert.That(result.Value.Reason, Is.Not.Empty, "Um veto SEM motivo e inutil.");
        });
    }

    // ── Helpers do dry-run ────────────────────────────────────────────────

    private static AutoTicketRuleDryRunQueryHandler BuildDryRunHandler(
        DiscoveryDbContext db,
        InMemoryAutoTicketRuleRepository? ruleRepo = null,
        ResolvedAutoTicketPolicy? policy = null)
    {
        ruleRepo ??= new InMemoryAutoTicketRuleRepository();
        var normalization = new MonitoringEventNormalizationService();

        return new AutoTicketRuleDryRunQueryHandler(
            new AutoTicketRuleEngineService(ruleRepo, normalization),
            ruleRepo,
            policy is null
                ? new FakeAutoTicketPolicyResolver()
                : new FakeAutoTicketPolicyResolver(policy),
            new AutoTicketSignalEvaluator(db, NullLogger<AutoTicketSignalEvaluator>.Instance),
            new DedupFingerprintService(normalization),
            db);
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("auto-ticket-dryrun-" + Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new DryRunTestDbContext(options);
    }

    private sealed class DryRunTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(SensorDefinition),
                typeof(Agent),
                typeof(AgentMonitoringEvent),
                typeof(AgentSensorState),
                typeof(ClientConfiguration),
                typeof(SiteConfiguration)
            };

            foreach (var type in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(type);
            }

            modelBuilder.Entity<SensorDefinition>(entity => entity.HasKey(s => s.Id));
            modelBuilder.Entity<Agent>(entity => entity.HasKey(a => a.Id));
            modelBuilder.Entity<AgentMonitoringEvent>(entity => entity.HasKey(e => e.Id));
            modelBuilder.Entity<AgentSensorState>(entity => entity.HasKey(s => s.Id));
            modelBuilder.Entity<ClientConfiguration>(entity => entity.HasKey(c => c.Id));
            modelBuilder.Entity<SiteConfiguration>(entity => entity.HasKey(s => s.Id));
        }
    }

    // ── Escopo: nível inválido NAO pode virar Global silenciosamente ──────
    // Antes, "Client" com escopoId malformado gravava a regra como GLOBAL —
    // ela passaria a abrir chamado para QUALQUER cliente. Escopo delimita o
    // alcance; errar para o lado mais amplo e o pior desfecho possivel.

    [Test]
    public async Task CreateRule_ShouldFail_WhenScopeLevelIsInvalid()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var handler = new CreateAutoTicketRuleCommandHandler(repo);

        var result = await handler.Handle(
            new CreateAutoTicketRuleCommand(
                "Regra", true, "NivelQueNaoExiste", Guid.NewGuid(), null, null,
                string.Empty, null, null, "Medium", 60, 0),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False, "Nivel invalido deve falhar, nao virar Global.");
            Assert.That(repo.Rules, Is.Empty, "Nenhuma regra pode ser criada nesse caso.");
        });
    }

    [Test]
    public async Task CreateRule_ShouldFail_WhenClientScopeHasNoScopeId()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var handler = new CreateAutoTicketRuleCommandHandler(repo);

        var result = await handler.Handle(
            new CreateAutoTicketRuleCommand(
                "Regra de cliente", true, "Client", null, null, null,
                string.Empty, null, null, "Medium", 60, 0),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False, "Client sem scopeId nao pode ser aceito.");
            Assert.That(repo.Rules, Is.Empty);
        });
    }

    [Test]
    public async Task CreateRule_ShouldAcceptGlobalScope_WithoutScopeId()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var handler = new CreateAutoTicketRuleCommandHandler(repo);

        var result = await handler.Handle(
            new CreateAutoTicketRuleCommand(
                "Regra global", true, "Global", null, "sensor:disk_space", null,
                string.Empty, null, null, "High", 60, 0),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True, "Global dispensa scopeId.");
            Assert.That(repo.Rules, Has.Count.EqualTo(1));
            Assert.That(repo.Rules[0].ScopeLevel, Is.EqualTo(AutoTicketScopeLevel.Global));
        });
    }

    [Test]
    public async Task CreateRule_ShouldAcceptClientScope_WithScopeId()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var handler = new CreateAutoTicketRuleCommandHandler(repo);
        var clientId = Guid.NewGuid();

        var result = await handler.Handle(
            new CreateAutoTicketRuleCommand(
                "Regra de cliente", true, "Client", clientId, null, null,
                string.Empty, null, null, "Medium", 60, 0),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(repo.Rules[0].ScopeLevel, Is.EqualTo(AutoTicketScopeLevel.Client));
            Assert.That(repo.Rules[0].ScopeId, Is.EqualTo(clientId),
                "O escopo precisa ser gravado — senao a regra vira global de fato.");
        });
    }

    // ── UPDATE: nivel e id de escopo sao UM par ───────────────────────────
    // Duas linhas independentes deixavam a regra com ScopeId NULL e nivel
    // Client => MatchesScope nunca casa => regra ATIVA que nunca dispara.

    [Test]
    public async Task UpdateRule_ShouldFail_WhenSwitchingToClientWithoutScopeId()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var global = new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "Global",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Global,
            ScopeId = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        repo.Rules.Add(global);

        var handler = new UpdateAutoTicketRuleCommandHandler(repo);
        var result = await handler.Handle(
            new UpdateAutoTicketRuleCommand(
                global.Id, null, null, "Client", null, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False,
                "Client sem scopeId deixaria a regra inerte — precisa falhar.");
            Assert.That(repo.Rules[0].ScopeLevel, Is.EqualTo(AutoTicketScopeLevel.Global),
                "A regra nao pode ficar em estado incoerente.");
        });
    }

    [Test]
    public async Task UpdateRule_ShouldAcceptSwitchToClient_WhenScopeIdIsProvided()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var clientId = Guid.NewGuid();
        var global = new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "Global",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Global,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        repo.Rules.Add(global);

        var handler = new UpdateAutoTicketRuleCommandHandler(repo);
        var result = await handler.Handle(
            new UpdateAutoTicketRuleCommand(
                global.Id, null, null, "Client", clientId, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True, "Caso legitimo precisa continuar funcionando.");
            Assert.That(repo.Rules[0].ScopeLevel, Is.EqualTo(AutoTicketScopeLevel.Client));
            Assert.That(repo.Rules[0].ScopeId, Is.EqualTo(clientId));
        });
    }

    [Test]
    public async Task UpdateRule_ShouldAllowKeepingExistingClientScope()
    {
        var repo = new InMemoryAutoTicketRuleRepository();
        var clientId = Guid.NewGuid();
        var rule = new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "De cliente",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Client,
            ScopeId = clientId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        repo.Rules.Add(rule);

        var handler = new UpdateAutoTicketRuleCommandHandler(repo);

        // Atualiza so o nome: o par de escopo continua valido.
        var result = await handler.Handle(
            new UpdateAutoTicketRuleCommand(
                rule.Id, "Novo nome", null, null, null, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(repo.Rules[0].Name, Is.EqualTo("Novo nome"));
            Assert.That(repo.Rules[0].ScopeId, Is.EqualTo(clientId), "Escopo preservado.");
        });
    }

    // ── Invariante COMPARTILHADA entre create e update ────────────────────
    // Os bugs 12 e 13 nasceram de duas copias da mesma validacao que
    // divergiram. Estes testes rodam a MESMA entrada nos DOIS handlers e
    // exigem o MESMO desfecho — se as copias voltarem a se separar, falha.

    [Test]
    public async Task CreateAndUpdate_ShouldAgree_WhenScopeLevelIsInvalid()
    {
        // CREATE
        var createRepo = new InMemoryAutoTicketRuleRepository();
        var createResult = await new CreateAutoTicketRuleCommandHandler(createRepo).Handle(
            new CreateAutoTicketRuleCommand(
                "Regra", true, "NivelInvalido", Guid.NewGuid(), null, null,
                string.Empty, null, null, "Medium", 60, 0),
            CancellationToken.None);

        // UPDATE
        var updateRepo = new InMemoryAutoTicketRuleRepository();
        var existing = new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "Existente",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Global,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        updateRepo.Rules.Add(existing);
        var updateResult = await new UpdateAutoTicketRuleCommandHandler(updateRepo).Handle(
            new UpdateAutoTicketRuleCommand(
                existing.Id, null, null, "NivelInvalido", null, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(createResult.IsSuccess, Is.False);
            Assert.That(updateResult.IsSuccess, Is.False,
                "Os dois caminhos precisam RECUSAR o mesmo nivel invalido.");
            Assert.That(createRepo.Rules, Is.Empty);
            Assert.That(updateRepo.Rules[0].ScopeLevel, Is.EqualTo(AutoTicketScopeLevel.Global),
                "Nenhum dos dois pode deixar estado incoerente.");
        });
    }

    [Test]
    public async Task CreateAndUpdate_ShouldAgree_WhenClientScopeHasNoId()
    {
        // CREATE
        var createRepo = new InMemoryAutoTicketRuleRepository();
        var createResult = await new CreateAutoTicketRuleCommandHandler(createRepo).Handle(
            new CreateAutoTicketRuleCommand(
                "Regra", true, "Client", null, null, null,
                string.Empty, null, null, "Medium", 60, 0),
            CancellationToken.None);

        // UPDATE
        var updateRepo = new InMemoryAutoTicketRuleRepository();
        var existing = new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "Existente",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Global,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        updateRepo.Rules.Add(existing);
        var updateResult = await new UpdateAutoTicketRuleCommandHandler(updateRepo).Handle(
            new UpdateAutoTicketRuleCommand(
                existing.Id, null, null, "Client", null, null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(createResult.IsSuccess, Is.False);
            Assert.That(updateResult.IsSuccess, Is.False,
                "Regra inerte (Client sem id) precisa ser recusada nos DOIS caminhos.");
        });
    }

    private sealed class InMemoryAutoTicketRuleRepository : IAutoTicketRuleRepository
    {
        public List<AutoTicketRule> Rules { get; } = [];

        public Task<AutoTicketRule?> GetByIdAsync(Guid id)
            => Task.FromResult(Rules.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<AutoTicketRule>> GetAllAsync(
            AutoTicketScopeLevel? scopeLevel = null,
            Guid? scopeId = null,
            bool? isEnabled = null,
            string? alertCode = null)
        {
            IEnumerable<AutoTicketRule> query = Rules;
            if (scopeLevel.HasValue) query = query.Where(r => r.ScopeLevel == scopeLevel.Value);
            if (scopeId.HasValue) query = query.Where(r => r.ScopeId == scopeId.Value);
            if (isEnabled.HasValue) query = query.Where(r => r.IsEnabled == isEnabled.Value);
            if (!string.IsNullOrWhiteSpace(alertCode)) query = query.Where(r => r.AlertCodeFilter == alertCode);
            return Task.FromResult<IReadOnlyList<AutoTicketRule>>(query.ToList());
        }

        public Task<AutoTicketRule> CreateAsync(AutoTicketRule rule)
        {
            Rules.Add(rule);
            return Task.FromResult(rule);
        }

        public Task<AutoTicketRule> UpdateAsync(AutoTicketRule rule)
        {
            var index = Rules.FindIndex(r => r.Id == rule.Id);
            if (index >= 0) Rules[index] = rule;
            return Task.FromResult(rule);
        }

        public Task<bool> DeleteAsync(Guid id)
        {
            var removed = Rules.RemoveAll(r => r.Id == id);
            return Task.FromResult(removed > 0);
        }
    }
}