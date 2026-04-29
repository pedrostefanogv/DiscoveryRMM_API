using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Discovery.Api.Services;

/// <summary>
/// Background service que processa artigos da KB em dois passos a cada 30s:
/// 1. Re-chunking: artigos publicados com last_chunked_at desatualizado
/// 2. Embedding: chunks sem embedding gerado
/// Ativação controlada pelas configurações de IA persistidas em banco.
/// </summary>
public class KnowledgeEmbeddingBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<KnowledgeEmbeddingBackgroundService> logger) : BackgroundService
{
    private readonly TimeSpan _activeInterval = TimeSpan.FromSeconds(Math.Max(10,
        configuration.GetValue<int?>("BackgroundJobs:KnowledgeEmbedding:ActiveIntervalSeconds") ?? 30));
    private readonly TimeSpan _idleInterval = TimeSpan.FromSeconds(Math.Max(30,
        configuration.GetValue<int?>("BackgroundJobs:KnowledgeEmbedding:IdleIntervalSeconds") ?? 600));
    private readonly TimeSpan _disabledInterval = TimeSpan.FromSeconds(Math.Max(60,
        configuration.GetValue<int?>("BackgroundJobs:KnowledgeEmbedding:DisabledIntervalSeconds") ?? 1800));
    private readonly TimeSpan _startupDelay = TimeSpan.FromSeconds(Math.Max(0,
        configuration.GetValue<int?>("BackgroundJobs:KnowledgeEmbedding:StartupDelaySeconds") ?? 10));
    private readonly int _batchSize = Math.Max(1,
        configuration.GetValue<int?>("BackgroundJobs:KnowledgeEmbedding:BatchSize") ?? 20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("KnowledgeEmbeddingBackgroundService iniciado.");

        await Task.Delay(_startupDelay, stoppingToken);

        var nextDelay = _activeInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                nextDelay = await ProcessCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                nextDelay = _activeInterval;
                logger.LogError(ex, "Erro no ciclo de embedding da KB. Próxima tentativa em {Interval}s", nextDelay.TotalSeconds);
            }

            await Task.Delay(nextDelay, stoppingToken);
        }

        logger.LogInformation("KnowledgeEmbeddingBackgroundService encerrado.");
    }

    private async Task<TimeSpan> ProcessCycleAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var articleRepository = scope.ServiceProvider.GetRequiredService<IKnowledgeArticleRepository>();
        var chunkRepository = scope.ServiceProvider.GetRequiredService<IKnowledgeChunkRepository>();
        var chunkingService = scope.ServiceProvider.GetRequiredService<IKnowledgeChunkingService>();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var resolver = scope.ServiceProvider.GetRequiredService<IConfigurationResolver>();

        var aiSettings = await resolver.GetAISettingsAsync();
        var enabledByAiSettings = aiSettings.EmbeddingEnabled && aiSettings.EmbeddingArticlesEnabled;
        if (!enabledByAiSettings)
        {
            logger.LogDebug("Knowledge embedding desativado para este ciclo (aiEnabled={AiEnabled}).", enabledByAiSettings);
            return _disabledInterval;
        }

        var processedAnyItem = false;

        // ── Passo 1: Re-chunking ────────────────────────────────────────
        var articlesNeedingChunking = await articleRepository.GetArticlesNeedingChunkingAsync(_batchSize, ct);

        if (articlesNeedingChunking.Count > 0)
        {
            processedAnyItem = true;
            logger.LogInformation("Re-chunking {Count} artigos...", articlesNeedingChunking.Count);

            foreach (var article in articlesNeedingChunking)
            {
                try
                {
                    var chunks = chunkingService.ChunkArticle(article);
                    await chunkRepository.ReplaceAllForArticleAsync(article.Id, chunks, ct);

                    article.LastChunkedAt = DateTime.UtcNow;
                    await articleRepository.UpdateAsync(article, ct);

                    logger.LogDebug("Artigo {Id} chunkado em {Count} partes.", article.Id, chunks.Count);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erro ao chunkar artigo {Id}", article.Id);
                }
            }
        }

        // ── Passo 2: Geração de Embeddings ─────────────────────────────
        var chunksWithoutEmbedding = await chunkRepository.GetChunksWithoutEmbeddingAsync(_batchSize, ct);

        if (chunksWithoutEmbedding.Count > 0)
        {
            processedAnyItem = true;
            logger.LogInformation("Gerando embeddings para {Count} chunks em batch...", chunksWithoutEmbedding.Count);

            try
            {
                // Prepara inputs e busca artigo associado ao primeiro chunk para resolver escopo
                var inputs = chunksWithoutEmbedding.Select(c =>
                    string.IsNullOrEmpty(c.SectionTitle)
                        ? c.Content
                        : $"{c.SectionTitle}\n\n{c.Content}").ToList();

                // Busca artigo do primeiro chunk para determinar escopo
                var firstChunk = chunksWithoutEmbedding[0];
                var article = await articleRepository.GetByIdAsync(firstChunk.ArticleId, ct);

                var credential = (article?.ClientId.HasValue == true || article?.SiteId.HasValue == true)
                    ? await scope.ServiceProvider.GetRequiredService<IAiCredentialResolver>()
                        .ResolveAsync(article.ClientId, article.SiteId, ct)
                    : null;

                var embBaseUrl = credential?.EffectiveEmbeddingBaseUrl
                    ?? (string.IsNullOrWhiteSpace(aiSettings.EmbeddingBaseUrl) ? aiSettings.BaseUrl : aiSettings.EmbeddingBaseUrl);
                var embApiKey = credential?.EffectiveEmbeddingApiKey
                    ?? (string.IsNullOrWhiteSpace(aiSettings.EmbeddingApiKey) ? aiSettings.ApiKey : aiSettings.EmbeddingApiKey);

                // Batch — uma única chamada HTTP para todos os chunks
                var allEmbeddings = await embeddingProvider.GenerateEmbeddingsAsync(
                    inputs,
                    aiSettings.EmbeddingModel,
                    embApiKey,
                    embBaseUrl,
                    ct);

                // Valida dimensão
                if (allEmbeddings.Count > 0 && allEmbeddings[0].Length != aiSettings.EmbeddingDimensions)
                {
                    logger.LogError(
                        "Dimensão do embedding ({Actual}) difere da configurada ({Expected}). Abortando batch.",
                        allEmbeddings[0].Length, aiSettings.EmbeddingDimensions);
                    return _activeInterval;
                }

                // Persiste
                for (int i = 0; i < chunksWithoutEmbedding.Count && i < allEmbeddings.Count; i++)
                {
                    var chunk = chunksWithoutEmbedding[i];
                    var floats = allEmbeddings[i];
                    await chunkRepository.UpdateEmbeddingAsync(chunk.Id, new Vector(floats), ct);
                }

                logger.LogInformation("Embeddings batch concluído: {Count} chunks processados.", allEmbeddings.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no batch de embeddings. Voltando para modo individual.");
                // Fallback: processa individualmente
                foreach (var chunk in chunksWithoutEmbedding)
                {
                    try
                    {
                        var embeddingInput = string.IsNullOrEmpty(chunk.SectionTitle)
                            ? chunk.Content
                            : $"{chunk.SectionTitle}\n\n{chunk.Content}";

                        var embBaseUrl = string.IsNullOrWhiteSpace(aiSettings.EmbeddingBaseUrl) ? aiSettings.BaseUrl : aiSettings.EmbeddingBaseUrl;
                        var embApiKey = string.IsNullOrWhiteSpace(aiSettings.EmbeddingApiKey) ? aiSettings.ApiKey : aiSettings.EmbeddingApiKey;
                        var floats = await embeddingProvider.GenerateEmbeddingAsync(embeddingInput, aiSettings.EmbeddingModel, embApiKey, embBaseUrl, ct);
                        await chunkRepository.UpdateEmbeddingAsync(chunk.Id, new Vector(floats), ct);
                    }
                    catch (Exception inner)
                    {
                        logger.LogError(inner, "Erro ao gerar embedding para chunk {Id}", chunk.Id);
                    }
                }
            }
        }

        return processedAnyItem ? _activeInterval : _idleInterval;
    }
}
