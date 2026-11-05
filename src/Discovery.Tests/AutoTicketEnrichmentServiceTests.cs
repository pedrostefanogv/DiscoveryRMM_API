using Discovery.Core.Configuration;
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
/// Cobre o enriquecimento por IA com um provedor FAKE — caminho que estava sem
/// cobertura de integração apesar de ser o mais arriscado (chama serviço externo
/// e molda o chamado).
///
/// O foco é a DEGRADAÇÃO: qualquer falha precisa virar fallback determinístico,
/// nunca exceção. O enriquecimento jamais pode derrubar a criação do chamado.
/// </summary>
[TestFixture]
public class AutoTicketEnrichmentServiceTests
{
    /// <summary>
    /// O circuito de IA é <c>static</c> (estado de processo, por design: protege
    /// o provedor entre requisições). Sem reset, os testes de falha o abrem e
    /// contaminam os demais — o que faria a suíte passar ou falhar por ORDEM.
    /// </summary>
    [SetUp]
    public void ResetCircuit() => AutoTicketEnrichmentService.ResetCircuit();

    [Test]
    public async Task Enrich_ShouldFallback_WhenAiModeIsOff()
    {
        var fake = new FakeLlmProvider("{\"title\":\"x\"}");
        var service = Build(fake, AiSettings(), CreateDb());

        var result = await service.EnrichAsync(BuildEvent(), Policy(AutoTicketAiMode.Off));

        Assert.Multiple(() =>
        {
            Assert.That(result.UsedFallback, Is.True);
            Assert.That(fake.CallCount, Is.EqualTo(0), "IA desligada não pode chamar o provedor.");
        });
    }

    [Test]
    public async Task Enrich_ShouldFallback_WhenAiIntegrationDisabled()
    {
        var fake = new FakeLlmProvider("{\"title\":\"x\"}");
        var settings = AiSettings();
        settings.Enabled = false;
        var service = Build(fake, settings, CreateDb());

        var result = await service.EnrichAsync(BuildEvent(), Policy(AutoTicketAiMode.Suggest));

        Assert.Multiple(() =>
        {
            Assert.That(result.UsedFallback, Is.True);
            Assert.That(fake.CallCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Enrich_ShouldFallback_WhenApiKeyMissing()
    {
        var fake = new FakeLlmProvider("{\"title\":\"x\"}");
        var settings = AiSettings();
        settings.ApiKey = null;
        var service = Build(fake, settings, CreateDb());

        var result = await service.EnrichAsync(BuildEvent(), Policy(AutoTicketAiMode.Suggest));

        Assert.Multiple(() =>
        {
            Assert.That(result.UsedFallback, Is.True);
            Assert.That(fake.CallCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Enrich_ShouldFallback_WhenProviderThrows()
    {
        var fake = new FakeLlmProvider(exception: new InvalidOperationException("provider down"));
        var service = Build(fake, AiSettings(), CreateDb());

        var result = await service.EnrichAsync(BuildEvent(), Policy(AutoTicketAiMode.Suggest));

        Assert.Multiple(() =>
        {
            Assert.That(result.UsedFallback, Is.True, "Exceção do provedor não pode escapar.");
            Assert.That(result.FallbackReason, Is.Not.Null);
        });
    }

    [Test]
    public async Task Enrich_ShouldFallback_WhenResponseIsNotJson()
    {
        var fake = new FakeLlmProvider("desculpe, nao consegui responder em JSON");
        var service = Build(fake, AiSettings(), CreateDb());

        var result = await service.EnrichAsync(BuildEvent(), Policy(AutoTicketAiMode.Suggest));

        Assert.That(result.UsedFallback, Is.True);
    }

    [Test]
    public async Task Enrich_ShouldNeverThrow_OnAnyProviderFailure()
    {
        var fake = new FakeLlmProvider(exception: new TimeoutException("demorou"));
        var service = Build(fake, AiSettings(), CreateDb());

        // A asserção real: o serviço precisa DEVOLVER fallback, não lançar.
        var result = await service.EnrichAsync(BuildEvent(), Policy(AutoTicketAiMode.AutoApply));

        Assert.That(result.UsedFallback, Is.True,
            "Falha do provedor nunca pode escapar como exceção.");
    }
    // ---- Caminho feliz ----

    [Test]
    public async Task Enrich_ShouldParseTitleDescriptionAndEvidence()
    {
        var fake = new FakeLlmProvider(
            "{\"title\":\"Disco C em 99%\",\"description\":\"O volume C atingiu 99%.\","
            + "\"evidence\":[\"Uso em 99%\",\"Limite critico 95%\"],"
            + "\"hypotheses\":[\"Logs sem rotacao\"],\"confidence\":0.82}");
        var service = Build(fake, AiSettings(), CreateDb());

        var result = await service.EnrichAsync(BuildEvent(), Policy(AutoTicketAiMode.Suggest));

        Assert.Multiple(() =>
        {
            Assert.That(result.UsedFallback, Is.False);
            Assert.That(result.Title, Is.EqualTo("Disco C em 99%"));
            Assert.That(result.Description, Does.Contain("99%"));
            Assert.That(result.Evidence, Has.Count.EqualTo(2));
            Assert.That(result.Hypotheses, Has.Count.EqualTo(1));
            Assert.That(result.Confidence, Is.EqualTo(0.82).Within(0.001));
        });
    }

    [Test]
    public async Task Enrich_ShouldTolerateMarkdownFences()
    {
        var fake = new FakeLlmProvider("```json {\"title\":\"Cercado\",\"confidence\":0.9} ```");
        var service = Build(fake, AiSettings(), CreateDb());

        var result = await service.EnrichAsync(BuildEvent(), Policy(AutoTicketAiMode.Suggest));

        Assert.Multiple(() =>
        {
            Assert.That(result.UsedFallback, Is.False);
            Assert.That(result.Title, Is.EqualTo("Cercado"));
        });
    }

    [Test]
    public async Task Enrich_ShouldTruncateOverlongTitle()
    {
        var longTitle = new string((char)120, 400);
        var fake = new FakeLlmProvider("{\"title\":\"" + longTitle + "\"}");
        var service = Build(fake, AiSettings(), CreateDb());

        var result = await service.EnrichAsync(BuildEvent(), Policy(AutoTicketAiMode.Suggest));

        Assert.That(result.Title!.Length, Is.LessThanOrEqualTo(200),
            "Titulo precisa caber na coluna de 200 do banco.");
    }

    // ---- Gate deterministico (antes do LLM) ----

    [Test]
    public async Task Enrich_ShouldNotCallProvider_WhenContentGateRejects()
    {
        var fake = new FakeLlmProvider("{\"title\":\"x\"}");
        var service = Build(fake, AiSettings(), CreateDb());

        var policy = Clone(Policy(AutoTicketAiMode.Suggest), gateFields: ["hostname"]);

        // Payload SEM o campo exigido pelo gate.
        var evt = BuildEvent();
        evt.PayloadJson = "{\"transition\":\"opened\"}";

        var result = await service.EnrichAsync(evt, policy);

        Assert.Multiple(() =>
        {
            Assert.That(result.UsedFallback, Is.True);
            Assert.That(result.FallbackReason, Does.Contain("gate").IgnoreCase);
            Assert.That(fake.CallCount, Is.EqualTo(0),
                "O gate deterministico precisa barrar ANTES de chamar o LLM.");
        });
    }

    [Test]
    public async Task Enrich_ShouldCallProvider_WhenContentGatePasses()
    {
        var fake = new FakeLlmProvider("{\"title\":\"Passou o gate\"}");
        var service = Build(fake, AiSettings(), CreateDb());

        var policy = Clone(Policy(AutoTicketAiMode.Suggest), gateFields: ["transition"]);

        var evt = BuildEvent();
        evt.PayloadJson = "{\"transition\":\"opened\",\"status\":\"Failing\"}";

        var result = await service.EnrichAsync(evt, policy);

        Assert.Multiple(() =>
        {
            Assert.That(fake.CallCount, Is.EqualTo(1));
            Assert.That(result.UsedFallback, Is.False);
        });
    }
    // ---- Redacao de PII ----

    [Test]
    public async Task Enrich_ShouldRedactSecretsBeforeSendingThePrompt()
    {
        var fake = new FakeLlmProvider("{\"title\":\"ok\"}");
        var service = Build(fake, AiSettings(), CreateDb());

        var evt = BuildEvent();
        evt.PayloadJson = "{\"password\":\"segredo123\",\"user\":\"joao\",\"transition\":\"opened\"}";

        await service.EnrichAsync(evt, Policy(AutoTicketAiMode.Suggest));

        Assert.Multiple(() =>
        {
            Assert.That(fake.LastPrompt, Does.Not.Contain("segredo123"),
                "Segredo NUNCA pode chegar ao provedor.");
            Assert.That(fake.LastPrompt, Does.Contain("[REDACTED]"));
            // Dado nao sensivel segue, senao o modelo perde contexto util.
            Assert.That(fake.LastPrompt, Does.Contain("joao"));
        });
    }

    [Test]
    public async Task Enrich_ShouldRespectEquipmentContextSwitch()
    {
        var fake = new FakeLlmProvider("{\"title\":\"ok\"}");
        var db = CreateDb();
        var evt = BuildEvent();
        SeedAgent(db, evt.AgentId, "SERVIDOR-SECRETO");

        var policy = Clone(Policy(AutoTicketAiMode.Suggest), allowEquipmentContext: false);
        var service = Build(fake, AiSettings(), db);

        await service.EnrichAsync(evt, policy);

        Assert.That(fake.LastPrompt, Does.Not.Contain("SERVIDOR-SECRETO"),
            "Com o contexto desligado, o nome do ativo nao pode sair.");
    }

    [Test]
    public async Task Enrich_ShouldAnonymizeAssetName_WhenConfigured()
    {
        var fake = new FakeLlmProvider("{\"title\":\"ok\"}");
        var db = CreateDb();
        var evt = BuildEvent();
        SeedAgent(db, evt.AgentId, "SERVIDOR-SECRETO");

        var policy = Clone(Policy(AutoTicketAiMode.Suggest), allowEquipmentContext: true, anonymize: true);
        var service = Build(fake, AiSettings(), db);

        await service.EnrichAsync(evt, policy);

        Assert.Multiple(() =>
        {
            Assert.That(fake.LastPrompt, Does.Not.Contain("SERVIDOR-SECRETO"));
            Assert.That(fake.LastPrompt, Does.Contain("asset-"), "Deve usar pseudonimo estavel.");
        });
    }

    [Test]
    public async Task Enrich_ShouldRedactNestedSecrets_NotJustTopLevel()
    {
        var fake = new FakeLlmProvider("{\"title\":\"ok\"}");
        var service = Build(fake, AiSettings(), CreateDb());

        var evt = BuildEvent();
        // Segredo aninhado dentro de objeto E dentro de array — o caso real,
        // porque payloads de sensor costumam ter estruturas, nao campos planos.
        evt.PayloadJson =
            "{\"transition\":\"opened\","
            + "\"config\":{\"password\":\"segredo-aninhado\"},"
            + "\"creds\":[{\"token\":\"token-em-array\"}]}";

        await service.EnrichAsync(evt, Policy(AutoTicketAiMode.Suggest));

        Assert.Multiple(() =>
        {
            Assert.That(fake.LastPrompt, Does.Not.Contain("segredo-aninhado"),
                "Segredo em objeto aninhado nao pode vazar.");
            Assert.That(fake.LastPrompt, Does.Not.Contain("token-em-array"),
                "Segredo dentro de array nao pode vazar.");
            Assert.That(fake.LastPrompt, Does.Contain("transition"),
                "O restante do payload deve seguir, senao a IA perde contexto.");
        });
    }

    [Test]
    public async Task Enrich_ShouldOmitPayload_WhenItIsNotValidJson()
    {
        var fake = new FakeLlmProvider("{\"title\":\"ok\"}");
        var service = Build(fake, AiSettings(), CreateDb());

        var evt = BuildEvent();
        evt.PayloadJson = "{ nao e json com senha=segredo123";

        await service.EnrichAsync(evt, Policy(AutoTicketAiMode.Suggest));

        Assert.That(fake.LastPrompt, Does.Not.Contain("segredo123"),
            "Payload invalido nao pode ser enviado cru — melhor omitir.");
    }

    // ---- Helpers ----

    private static AutoTicketEnrichmentService Build(
        ILlmProvider provider, AIIntegrationSettings settings, DiscoveryDbContext db)
        => new(db, provider, new FakeConfigResolver(settings),
               new CapturingLogger<AutoTicketEnrichmentService>());

    /// <summary>Captura o warning para diagnosticar falhas do provedor no teste.</summary>
    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public static string? LastWarning { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                LastWarning = formatter(state, exception) + " || " + exception;
        }
    }

    private static AIIntegrationSettings AiSettings() => new()
    {
        Enabled = true,
        ApiKey = "test-key",
        ChatModel = "test-model",
        Provider = "openrouter",
        TimeoutMs = 30000
    };

    private static ResolvedAutoTicketPolicy Policy(AutoTicketAiMode mode) => new()
    {
        Enabled = true,
        Mode = AutoTicketScopeMode.Active,
        AiMode = mode,
        AiMinConfidence = 0.6,
        AiSuggestibleFields = ["Title", "Description"],
        AiAllowEquipmentContext = true,
        AiRedactFields = ["password", "secret", "token", "apikey"]
    };

    private static ResolvedAutoTicketPolicy Clone(
        ResolvedAutoTicketPolicy p,
        string[]? gateFields = null,
        bool? allowEquipmentContext = null,
        bool? anonymize = null)
        => new()
        {
            Enabled = p.Enabled,
            Mode = p.Mode,
            AiMode = p.AiMode,
            AiMinConfidence = p.AiMinConfidence,
            AiSuggestibleFields = p.AiSuggestibleFields,
            AiAllowEquipmentContext = allowEquipmentContext ?? p.AiAllowEquipmentContext,
            AiAnonymizeAssetNames = anonymize ?? p.AiAnonymizeAssetNames,
            AiRedactFields = p.AiRedactFields,
            AiContentGateFields = gateFields ?? p.AiContentGateFields
        };

    private static AgentMonitoringEvent BuildEvent() => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        AlertCode = "sensor:disk_space",
        Severity = MonitoringEventSeverity.Critical,
        Title = "Disk Full",
        Message = "Drive C at 99%.",
        PayloadJson = "{\"transition\":\"opened\",\"status\":\"Failing\"}",
        Source = MonitoringEventSource.HealthCheck,
        OccurredAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    private static void SeedAgent(DiscoveryDbContext db, Guid agentId, string hostname)
    {
        db.Agents.Add(new Agent
        {
            Id = agentId,
            Hostname = hostname,
            OperatingSystem = "Windows Server 2022",
            Status = AgentStatus.Online,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("auto-ticket-enrich-" + Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new EnrichTestDbContext(options);
    }

    /// <summary>Provedor fake: registra o prompt e devolve o que for combinado.</summary>
    private sealed class FakeLlmProvider(string? response = null, Exception? exception = null)
        : ILlmProvider
    {
        public int CallCount { get; private set; }
        public string? LastPrompt { get; private set; }

        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, List<LlmMessage> messages, LlmOptions options,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPrompt = messages.Count > 0 ? messages[0].Content : null;

            if (exception is not null) throw exception;

            return Task.FromResult(new LlmResponse(
                response ?? "{}", TokensUsed: 42, ModelVersion: "test-model-v1"));
        }

        public IAsyncEnumerable<string> StreamAsync(
            string systemPrompt, List<LlmMessage> messages, LlmOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<LlmStreamEvent> StreamWithToolsAsync(
            string systemPrompt, List<LlmMessage> messages, LlmOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeConfigResolver(AIIntegrationSettings settings) : IConfigurationResolver
    {
        public Task<AIIntegrationSettings> GetAISettingsAsync() => Task.FromResult(settings);
        public Task<ServerConfiguration> GetServerAsync() => Task.FromResult(new ServerConfiguration());
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => Task.FromResult<ClientConfiguration?>(null);
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => Task.FromResult<SiteConfiguration?>(null);
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => Task.FromResult<T?>(default);
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => Task.FromResult<T?>(null);
        public Task<BrandingSettings> GetBrandingSettingsAsync() => Task.FromResult(new BrandingSettings());
        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<ResolvedConfiguration> ResolveForClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(
            Guid? clientId, CancellationToken ct = default)
            => Task.FromResult(new BackgroundProcessingSettings());
        public Task ValidateInheritanceAsync() => Task.CompletedTask;
        public void ClearCache() { }
    }

    private sealed class EnrichTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Agent),
                typeof(SensorDefinition),
                typeof(AgentSensorState),
                typeof(Department)
            };

            foreach (var type in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(type);
            }

            modelBuilder.Entity<Agent>(entity => entity.HasKey(a => a.Id));
            modelBuilder.Entity<SensorDefinition>(entity => entity.HasKey(s => s.Id));
            modelBuilder.Entity<AgentSensorState>(entity => entity.HasKey(s => s.Id));
            modelBuilder.Entity<Department>(entity => entity.HasKey(d => d.Id));
        }
    }
}