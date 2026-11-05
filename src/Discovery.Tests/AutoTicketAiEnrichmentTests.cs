using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Cobre o enriquecimento por IA (Fase 5) e, principalmente, as SALVAGUARDAS.
///
/// O ponto central: a IA sugere, mas quem aplica é código nosso, com validação.
/// O teste mais importante aqui é o PISO DE PRIORIDADE — sem ele, o modelo
/// poderia transformar um incidente crítico em chamado de baixa prioridade.
/// </summary>
[TestFixture]
public class AutoTicketAiEnrichmentTests
{
    // ── Piso de prioridade (a salvaguarda mais crítica) ────────────────────

    [Test]
    public async Task Apply_ShouldBlockAiDowngrade_WhenSignalEscalated()
    {
        await using var db = CreateDb();
        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);

        var enrichment = WithSuggestion(
            AutoTicketSuggestionField.Priority, "Low", confidence: 0.95);

        var result = await applier.ApplyAsync(
            BuildEvent(), AutoApplyPolicy(), enrichment,
            deterministicPriority: TicketPriority.Critical,
            isEscalation: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Priority, Is.EqualTo(TicketPriority.Critical),
                "A IA NUNCA pode rebaixar um chamado escalado.");
            Assert.That(result.PriorityFloored, Is.True);
            Assert.That(result.Suggestions.Single().Applied, Is.False);
            Assert.That(result.Suggestions.Single().NotAppliedReason, Does.Contain("floor"));
        });
    }

    [Test]
    public async Task Apply_ShouldAllowAiUpgrade_WhenSignalEscalated()
    {
        await using var db = CreateDb();
        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);

        var enrichment = WithSuggestion(
            AutoTicketSuggestionField.Priority, "Critical", confidence: 0.95);

        var result = await applier.ApplyAsync(
            BuildEvent(), AutoApplyPolicy(), enrichment,
            deterministicPriority: TicketPriority.High,
            isEscalation: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Priority, Is.EqualTo(TicketPriority.Critical),
                "Elevar é permitido — o piso só impede rebaixar.");
            Assert.That(result.Suggestions.Single().Applied, Is.True);
        });
    }

    [Test]
    public async Task Apply_ShouldAllowDowngrade_WhenNotEscalating()
    {
        await using var db = CreateDb();
        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);

        var enrichment = WithSuggestion(
            AutoTicketSuggestionField.Priority, "Low", confidence: 0.95);

        var result = await applier.ApplyAsync(
            BuildEvent(), AutoApplyPolicy(), enrichment,
            deterministicPriority: TicketPriority.High,
            isEscalation: false);

        Assert.That(result.Priority, Is.EqualTo(TicketPriority.Low),
            "Fora de escalação não há piso: a IA pode ajustar para baixo.");
    }

    // ── Modo Suggest vs AutoApply ─────────────────────────────────────────

    [Test]
    public async Task Apply_ShouldNotApplyAnything_InSuggestMode()
    {
        await using var db = CreateDb();
        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);
        var suggestPolicy = new ResolvedAutoTicketPolicy
        {
            AiMode = AutoTicketAiMode.Suggest,
            AiMinConfidence = 0.60,
            AiSuggestibleFields = ["Priority"]
        };

        var result = await applier.ApplyAsync(
            BuildEvent(), suggestPolicy,
            WithSuggestion(AutoTicketSuggestionField.Priority, "Critical", 0.99),
            deterministicPriority: TicketPriority.Low,
            isEscalation: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Priority, Is.EqualTo(TicketPriority.Low), "Suggest não altera nada.");
            Assert.That(result.Suggestions.Single().Applied, Is.False);
            Assert.That(result.Suggestions.Single().NotAppliedReason, Does.Contain("human review"));
        });
    }

    [Test]
    public async Task Apply_ShouldRejectSuggestion_WhenConfidenceBelowThreshold()
    {
        await using var db = CreateDb();
        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);

        var result = await applier.ApplyAsync(
            BuildEvent(), AutoApplyPolicy(),
            WithSuggestion(AutoTicketSuggestionField.Priority, "Critical", confidence: 0.30),
            deterministicPriority: TicketPriority.Low,
            isEscalation: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Priority, Is.EqualTo(TicketPriority.Low));
            Assert.That(result.Suggestions.Single().NotAppliedReason, Does.Contain("below threshold"));
        });
    }

    [Test]
    public async Task Apply_ShouldIgnoreFieldNotEnabledInPolicy()
    {
        await using var db = CreateDb();
        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);
        var policy = new ResolvedAutoTicketPolicy
        {
            AiMode = AutoTicketAiMode.AutoApply,
            AiMinConfidence = 0.60,
            AiSuggestibleFields = ["Title"] // Priority NÃO está habilitado
        };

        var result = await applier.ApplyAsync(
            BuildEvent(), policy,
            WithSuggestion(AutoTicketSuggestionField.Priority, "Critical", 0.99),
            deterministicPriority: TicketPriority.Low,
            isEscalation: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Priority, Is.EqualTo(TicketPriority.Low));
            Assert.That(result.Suggestions.Single().NotAppliedReason, Does.Contain("not enabled"));
        });
    }

    // ── Validação de roteamento ───────────────────────────────────────────

    [Test]
    public async Task Apply_ShouldDiscardDepartment_WhenItDoesNotExist()
    {
        await using var db = CreateDb();
        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);

        var result = await applier.ApplyAsync(
            BuildEvent(), AutoApplyPolicy(),
            WithSuggestion(AutoTicketSuggestionField.Department, Guid.NewGuid().ToString(), 0.99),
            deterministicPriority: TicketPriority.Medium,
            isEscalation: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.DepartmentId, Is.Null, "Departamento inexistente é descartado.");
            Assert.That(result.Suggestions.Single().Applied, Is.False);
            Assert.That(result.Suggestions.Single().NotAppliedReason, Does.Contain("does not exist").IgnoreCase);
        });
    }

    [Test]
    public async Task Apply_ShouldAcceptDepartment_WhenItExistsForTheClient()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        db.Departments.Add(new Department { Id = deptId, ClientId = clientId, Name = "Infra" });
        await db.SaveChangesAsync();

        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);

        var result = await applier.ApplyAsync(
            BuildEvent(clientId), AutoApplyPolicy(),
            WithSuggestion(AutoTicketSuggestionField.Department, deptId.ToString(), 0.99),
            deterministicPriority: TicketPriority.Medium,
            isEscalation: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.DepartmentId, Is.EqualTo(deptId));
            Assert.That(result.Suggestions.Single().Applied, Is.True);
        });
    }

    [Test]
    public async Task Apply_ShouldRejectDepartment_FromAnotherClient()
    {
        await using var db = CreateDb();
        var otherClientId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        db.Departments.Add(new Department { Id = deptId, ClientId = otherClientId, Name = "De outro cliente" });
        await db.SaveChangesAsync();

        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);

        var result = await applier.ApplyAsync(
            BuildEvent(Guid.NewGuid()), AutoApplyPolicy(),
            WithSuggestion(AutoTicketSuggestionField.Department, deptId.ToString(), 0.99),
            deterministicPriority: TicketPriority.Medium,
            isEscalation: false);

        Assert.That(result.DepartmentId, Is.Null,
            "Não pode rotear para departamento de OUTRO cliente — vazamento entre tenants.");
    }

    [Test]
    public async Task Apply_ShouldRejectInvalidPriorityString()
    {
        await using var db = CreateDb();
        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);

        var result = await applier.ApplyAsync(
            BuildEvent(), AutoApplyPolicy(),
            WithSuggestion(AutoTicketSuggestionField.Priority, "Banana", 0.99),
            deterministicPriority: TicketPriority.Medium,
            isEscalation: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Priority, Is.EqualTo(TicketPriority.Medium));
            Assert.That(result.Suggestions.Single().NotAppliedReason,
                Does.Contain("valid priority").IgnoreCase);
        });
    }

    // ── Fallback / degradação ─────────────────────────────────────────────

    [Test]
    public void Fallback_ShouldBeMarked_SoTheDeterministicTextIsKept()
    {
        var fallback = AutoTicketEnrichment.Fallback("test");

        Assert.Multiple(() =>
        {
            Assert.That(fallback.UsedFallback, Is.True);
            Assert.That(fallback.Title, Is.Null);
            Assert.That(fallback.Description, Is.Null);
            Assert.That(fallback.Suggestions, Is.Empty);
            Assert.That(fallback.FallbackReason, Is.EqualTo("test"));
        });
    }

    [Test]
    public void SuggestibleFieldsSafe_ShouldFallBackToTextOnly_WhenPolicySaysNothing()
    {
        var policy = new ResolvedAutoTicketPolicy();

        Assert.That(policy.SuggestibleFieldsSafe(), Is.EquivalentTo(new[] { "Title", "Description" }),
            "Sem configuração, a IA só pode sugerir TEXTO — nunca roteamento.");
    }

    [Test]
    public void SuggestibleFieldsSafe_ShouldHonourConfiguredFields()
    {
        var policy = new ResolvedAutoTicketPolicy
        {
            AiSuggestibleFields = ["Priority", "Department"]
        };

        Assert.That(policy.SuggestibleFieldsSafe(), Is.EquivalentTo(new[] { "Priority", "Department" }));
    }

    // ---- Precedencia de roteamento ----
    // A regra e MAIS ESPECIFICA que o default da politica. Semear o applier
    // com policy.DefaultDepartmentId fazia o default sobrepor a regra.

    [Test]
    public async Task Apply_ShouldNotSeedDepartment_FromPolicyDefault()
    {
        await using var db = CreateDb();
        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);

        var policyDefaultDept = Guid.NewGuid();
        var policy = new ResolvedAutoTicketPolicy
        {
            AiMode = AutoTicketAiMode.Suggest,
            AiMinConfidence = 0.6,
            AiSuggestibleFields = ["Department"],
            DefaultDepartmentId = policyDefaultDept
        };

        // Nenhuma sugestao da IA: nada deve ser "aplicado".
        var result = await applier.ApplyAsync(
            BuildEvent(), policy, new AutoTicketEnrichment { Confidence = 0.9 },
            deterministicPriority: TicketPriority.Medium, isEscalation: false);

        Assert.That(result.DepartmentId, Is.Null,
            "O default da politica NAO pode sair como se fosse sugestao aplicada — " +
            "senao ele sobrepoe o departamento definido pela regra.");
    }

    [Test]
    public async Task Apply_ShouldNotSeedCategory_FromPolicyDefault()
    {
        await using var db = CreateDb();
        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);

        var policy = new ResolvedAutoTicketPolicy
        {
            AiMode = AutoTicketAiMode.Suggest,
            AiMinConfidence = 0.6,
            AiSuggestibleFields = ["Category"],
            DefaultCategory = "DefaultDaPolitica"
        };

        var result = await applier.ApplyAsync(
            BuildEvent(), policy, new AutoTicketEnrichment { Confidence = 0.9 },
            deterministicPriority: TicketPriority.Medium, isEscalation: false);

        Assert.That(result.Category, Is.Null,
            "A categoria default da politica nao pode mascarar a categoria da regra.");
    }

    [Test]
    public async Task Apply_ShouldReturnDepartment_OnlyWhenAiActuallyAppliedIt()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var deptId = Guid.NewGuid();
        db.Departments.Add(new Department { Id = deptId, ClientId = clientId, Name = "Infra" });
        await db.SaveChangesAsync();

        var applier = new AutoTicketSuggestionApplier(db, NullLogger<AutoTicketSuggestionApplier>.Instance);
        var policy = new ResolvedAutoTicketPolicy
        {
            AiMode = AutoTicketAiMode.AutoApply,
            AiMinConfidence = 0.6,
            AiSuggestibleFields = ["Department"]
        };

        var result = await applier.ApplyAsync(
            BuildEvent(clientId), policy,
            WithSuggestion(AutoTicketSuggestionField.Department, deptId.ToString(), 0.99),
            deterministicPriority: TicketPriority.Medium, isEscalation: false);

        Assert.That(result.DepartmentId, Is.EqualTo(deptId),
            "Quando a IA realmente aplica, o valor precisa voltar.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ResolvedAutoTicketPolicy AutoApplyPolicy() => new()
    {
        AiMode = AutoTicketAiMode.AutoApply,
        AiMinConfidence = 0.60,
        AiSuggestibleFields = ["Priority", "Department", "Category"]
    };

    private static AutoTicketEnrichment WithSuggestion(
        AutoTicketSuggestionField field, string value, double confidence)
        => new()
        {
            Confidence = confidence,
            Suggestions =
            [
                new AutoTicketSuggestionItem
                {
                    Field = field,
                    SuggestedValue = value,
                    Confidence = confidence,
                    Rationale = "test"
                }
            ]
        };

    private static AgentMonitoringEvent BuildEvent(Guid? clientId = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId ?? Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        AlertCode = "sensor:disk_space",
        Severity = MonitoringEventSeverity.Critical,
        Title = "Disk Full",
        Message = "Drive C at 99%.",
        Source = MonitoringEventSource.HealthCheck,
        OccurredAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"auto-ticket-ai-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new AiTestDbContext(options);
    }

    /// <summary>DbContext de teste: ignora entidades não usadas (o modelo completo
    /// tem tipos específicos do Postgres, como a coluna vetorial).</summary>
    private sealed class AiTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Department), typeof(AgentMonitoringEvent) };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Department>(entity => entity.HasKey(d => d.Id));
            modelBuilder.Entity<AgentMonitoringEvent>(entity => entity.HasKey(e => e.Id));
        }
    }
}