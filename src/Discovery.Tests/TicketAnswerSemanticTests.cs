using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Discovery.Tests;

/// <summary>
/// Fase 3 — embeddings das respostas do questionário:
/// - processador: dedupe de textos, pulo do que não agrega, teto por ciclo e
///   não-gravação em divergência de dimensão;
/// - busca: modo semântico com dedupe por chamado, degradação para texto e
///   comportamento com a flag desligada.
/// (O caminho SQL/pgvector é coberto por teste de integração em Postgres.)
/// </summary>
public class TicketAnswerSemanticTests
{
    // ── Processador ──────────────────────────────────────────────────────

    [Test]
    public async Task Processor_ShouldDedupeIdenticalTexts()
    {
        var repository = new FakeTicketAnswerRepository();
        var repeated = new TicketAnswerEmbeddingJobItem(Guid.NewGuid(), Guid.NewGuid(), "obs", "Observação", "Não consigo acessar a VPN", null, null);
        repository.Pending.Add(repeated);
        repository.Pending.Add(repeated with { AnswerId = Guid.NewGuid(), TicketId = Guid.NewGuid() });
        repository.Pending.Add(new TicketAnswerEmbeddingJobItem(Guid.NewGuid(), Guid.NewGuid(), "email", "E-mail", "ana@empresa.com.br", null, null));

        var provider = new FakeEmbeddingProvider();
        var processor = new TicketAnswerEmbeddingProcessor(repository, provider, new FakeCredentialResolver(), NullLogger<TicketAnswerEmbeddingProcessor>.Instance);

        var processed = await processor.ProcessAsync(Settings());

        Assert.Multiple(() =>
        {
            Assert.That(processed, Is.EqualTo(3));
            Assert.That(provider.LastInputs, Has.Count.EqualTo(2), "textos repetidos devem gerar uma única entrada");
            Assert.That(repository.Updates, Has.Count.EqualTo(3), "cada resposta recebe seu vetor");
        });
    }

    [Test]
    public async Task Processor_ShouldSkipAndMarkShortAnswers()
    {
        var repository = new FakeTicketAnswerRepository();
        var shortAnswer = new TicketAnswerEmbeddingJobItem(Guid.NewGuid(), Guid.NewGuid(), "aceite", "Aceite", "Sim", null, null);
        repository.Pending.Add(shortAnswer);

        var provider = new FakeEmbeddingProvider();
        var processor = new TicketAnswerEmbeddingProcessor(repository, provider, new FakeCredentialResolver(), NullLogger<TicketAnswerEmbeddingProcessor>.Instance);

        var processed = await processor.ProcessAsync(Settings());

        Assert.Multiple(() =>
        {
            Assert.That(processed, Is.Zero);
            Assert.That(repository.MarkedProcessed, Contains.Item(shortAnswer.AnswerId), "resposta pulada não pode voltar à fila");
            Assert.That(repository.Updates, Is.Empty);
        });
    }

    [Test]
    public async Task Processor_ShouldNotWriteOnDimensionMismatch()
    {
        var repository = new FakeTicketAnswerRepository();
        repository.Pending.Add(new TicketAnswerEmbeddingJobItem(Guid.NewGuid(), Guid.NewGuid(), "obs", "Observação", "Descrição longa o suficiente aqui", null, null));

        var provider = new FakeEmbeddingProvider { Dimensions = 7 };
        var processor = new TicketAnswerEmbeddingProcessor(repository, provider, new FakeCredentialResolver(), NullLogger<TicketAnswerEmbeddingProcessor>.Instance);

        var processed = await processor.ProcessAsync(Settings(dimensions: 3));

        Assert.Multiple(() =>
        {
            Assert.That(processed, Is.Zero);
            Assert.That(repository.Updates, Is.Empty, "não gravar enquanto a dimensão não estiver realinhada");
        });
    }

    [Test]
    public async Task Processor_ShouldRespectPerCycleBudget()
    {
        var repository = new FakeTicketAnswerRepository();
        for (var i = 0; i < 60; i++)
        {
            repository.Pending.Add(new TicketAnswerEmbeddingJobItem(
                Guid.NewGuid(), Guid.NewGuid(), $"q{i}", "Pergunta", $"Resposta suficientemente longa {i}", null, null));
        }

        var provider = new FakeEmbeddingProvider();
        var processor = new TicketAnswerEmbeddingProcessor(repository, provider, new FakeCredentialResolver(), NullLogger<TicketAnswerEmbeddingProcessor>.Instance);

        var processed = await processor.ProcessAsync(Settings());

        Assert.Multiple(() =>
        {
            Assert.That(processed, Is.EqualTo(TicketAnswerEmbeddingProcessor.MaxAnswersPerCycle));
            Assert.That(repository.Pending, Has.Count.EqualTo(10), "o restante fica para os próximos ciclos");
        });
    }

    // ── Busca ────────────────────────────────────────────────────────────

    [Test]
    public async Task Search_ShouldReturnSemanticHitsDedupedByTicket()
    {
        var ticketA = Guid.NewGuid();
        var ticketB = Guid.NewGuid();
        var repository = new FakeTicketAnswerRepository
        {
            SemanticHits =
            [
                Hit(ticketA, "Acesso VPN", "Sistema", "Softwares de terceiros", 0.20),
                Hit(ticketA, "Acesso VPN", "Perfil", "Administrador", 0.10),
                Hit(ticketB, "Impressora", "Setor", "Financeiro", 0.30),
            ],
        };

        var service = BuildSearchService(repository, new FakeEmbeddingProvider(), Settings());

        var result = await service.SearchAsync(new TicketAnswerSearchRequest("vpn"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Mode, Is.EqualTo("semantic"));
            Assert.That(result.Hits, Has.Count.EqualTo(2), "um hit por chamado (melhor resposta)");
            Assert.That(result.Hits[0].TicketId, Is.EqualTo(ticketA));
            Assert.That(result.Hits[0].Distance, Is.EqualTo(0.10).Within(0.0001));
        });
    }

    [Test]
    public async Task Search_ShouldFallBackToKeywordWhenProviderFails()
    {
        var repository = new FakeTicketAnswerRepository
        {
            KeywordHits = [Hit(Guid.NewGuid(), "Chamado por texto", "Sistema", "Interno", 0)],
        };
        var provider = new FakeEmbeddingProvider { Throw = true };

        var service = BuildSearchService(repository, provider, Settings());
        var result = await service.SearchAsync(new TicketAnswerSearchRequest("vpn"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Mode, Is.EqualTo("keyword"));
            Assert.That(result.Hits, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Search_ShouldReportDisabledWhenFlagOff()
    {
        var repository = new FakeTicketAnswerRepository
        {
            KeywordHits = [Hit(Guid.NewGuid(), "Chamado por texto", "Sistema", "Interno", 0)],
        };
        var provider = new FakeEmbeddingProvider();

        var service = BuildSearchService(repository, provider, Settings(ticketAnswersEnabled: false));
        var result = await service.SearchAsync(new TicketAnswerSearchRequest("vpn"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Mode, Is.EqualTo("disabled"));
            Assert.That(provider.LastInputs, Is.Empty, "não deve chamar o provedor com a flag desligada");
            Assert.That(result.Hits, Has.Count.EqualTo(1), "ainda responde por texto");
        });
    }

    [Test]
    public async Task Search_ShouldReturnEmptyForBlankQuery()
    {
        var service = BuildSearchService(new FakeTicketAnswerRepository(), new FakeEmbeddingProvider(), Settings());
        var result = await service.SearchAsync(new TicketAnswerSearchRequest("   "));

        Assert.That(result.Hits, Is.Empty);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static AIIntegrationSettings Settings(bool ticketAnswersEnabled = true, int dimensions = 3) => new()
    {
        EmbeddingEnabled = true,
        EmbeddingArticlesEnabled = false,
        EmbeddingTicketAnswersEnabled = ticketAnswersEnabled,
        EmbeddingDimensions = dimensions,
        EmbeddingModel = "test-embedding",
        MinSimilarityScore = 0.0,
    };

    private static TicketAnswerSearchHit Hit(Guid ticketId, string title, string label, string value, double distance)
        => new(ticketId, title, null, null, label.ToLowerInvariant(), label, value, distance, DateTime.UtcNow);

    private static TicketAnswerSearchService BuildSearchService(
        ITicketAnswerRepository repository,
        IEmbeddingProvider provider,
        AIIntegrationSettings settings)
        => new(
            repository,
            provider,
            new FakeConfigurationResolver(settings),
            new FakeCredentialResolver(),
            new FakeScopeContext(),
            NullLogger<TicketAnswerSearchService>.Instance);

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeTicketAnswerRepository : ITicketAnswerRepository
    {
        public List<TicketAnswerEmbeddingJobItem> Pending { get; } = [];
        public IReadOnlyList<TicketAnswerSearchHit> SemanticHits { get; set; } = Array.Empty<TicketAnswerSearchHit>();
        public IReadOnlyList<TicketAnswerSearchHit> KeywordHits { get; set; } = Array.Empty<TicketAnswerSearchHit>();
        public List<TicketAnswerEmbeddingUpdate> Updates { get; } = [];
        public List<Guid> MarkedProcessed { get; } = [];

        public Task<IReadOnlyList<TicketAnswerEmbeddingJobItem>> GetWithoutEmbeddingAsync(int limit, CancellationToken ct = default)
        {
            var batch = Pending.Take(limit).ToList();
            Pending.RemoveRange(0, batch.Count);
            return Task.FromResult<IReadOnlyList<TicketAnswerEmbeddingJobItem>>(batch);
        }

        public Task UpdateEmbeddingsAsync(IReadOnlyList<TicketAnswerEmbeddingUpdate> updates, CancellationToken ct = default)
        {
            Updates.AddRange(updates);
            return Task.CompletedTask;
        }

        public Task MarkProcessedWithoutEmbeddingAsync(IReadOnlyList<Guid> answerIds, CancellationToken ct = default)
        {
            MarkedProcessed.AddRange(answerIds);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TicketAnswerSearchHit>> SearchKeywordAsync(
            string term, bool hasGlobalAccess, IReadOnlyCollection<Guid> allowedClientIds,
            IReadOnlyCollection<Guid> allowedSiteIds, Guid? templateId, string? questionKey,
            int limit, CancellationToken ct = default)
            => Task.FromResult(KeywordHits);

        public Task<IReadOnlyList<TicketAnswerSearchHit>> SearchSemanticAsync(
            Vector queryEmbedding, bool hasGlobalAccess, IReadOnlyCollection<Guid> allowedClientIds,
            IReadOnlyCollection<Guid> allowedSiteIds, Guid? templateId, string? questionKey,
            int limit, double minSimilarity, CancellationToken ct = default)
            => Task.FromResult(SemanticHits);
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions { get; set; } = 3;
        public bool Throw { get; set; }
        public List<string> LastInputs { get; private set; } = [];

        public Task<float[]> GenerateEmbeddingAsync(
            string text, string? modelOverride = null, string? apiKeyOverride = null,
            string? baseUrlOverride = null, CancellationToken ct = default)
            => Throw
                ? Task.FromException<float[]>(new InvalidOperationException("provedor indisponível"))
                : Task.FromResult(new float[Dimensions]);

        public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
            IReadOnlyList<string> inputs, string? modelOverride = null, string? apiKeyOverride = null,
            string? baseUrlOverride = null, CancellationToken ct = default)
        {
            LastInputs = inputs.ToList();
            if (Throw)
                return Task.FromException<IReadOnlyList<float[]>>(new InvalidOperationException("provedor indisponível"));

            IReadOnlyList<float[]> result = inputs.Select(_ => new float[Dimensions]).ToList();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCredentialResolver : IAiCredentialResolver
    {
        public Task<ResolvedCredential?> ResolveAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
            => Task.FromResult<ResolvedCredential?>(null);

        public Task<string?> ResolveEmbeddingApiKeyAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
            => Task.FromResult<string?>("test-key");

        public Task<string?> ResolveChatApiKeyAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
            => Task.FromResult<string?>("test-key");
    }

    private sealed class FakeConfigurationResolver(AIIntegrationSettings settings) : IConfigurationResolver
    {
        public Task<AIIntegrationSettings> GetAISettingsAsync() => Task.FromResult(settings);
        public void ClearCache() { }
        public Task<ServerConfiguration> GetServerAsync() => throw new NotSupportedException();
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<AutoUpdateSettings> GetAutoUpdateSettingsAsync(string level, Guid? targetId = null) => throw new NotSupportedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => Task.CompletedTask;
    }

    private sealed class FakeScopeContext : IScopeContext
    {
        public UserScopeAccess Access { get; set; } = new() { HasGlobalAccess = true };
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }

        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action) => Task.FromResult(Access);
        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action) => Task.FromResult(Access.HasGlobalAccess);
        public void SetUserId(Guid userId) { }
    }
}
