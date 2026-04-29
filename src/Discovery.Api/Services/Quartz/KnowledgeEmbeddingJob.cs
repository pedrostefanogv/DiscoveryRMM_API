using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pgvector;
using Quartz;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job unificado que substitui <see cref="Services.KnowledgeEmbeddingBackgroundService"/>
/// e <see cref="Services.KnowledgeEmbeddingQueueBackgroundService"/>.
/// Consolida re-chunking, geração de embeddings em batch e processamento da fila
/// LISTEN/NOTIFY em um único job com <see cref="DisallowConcurrentExecutionAttribute"/>.
///
/// Schedule: a cada 30s (SimpleSchedule), com trigger manual via JobsController.
/// Métricas: OpenTelemetry Meter para chunks processados e latência.
/// </summary>
[DisallowConcurrentExecution]
public sealed class KnowledgeEmbeddingJob : IJob
{
    public static readonly JobKey Key = new("knowledge-embedding", "kb");

    private static readonly Meter Meter = new("Discovery.KnowledgeEmbedding", "1.0");
    private static readonly Counter<long> ChunksProcessedCounter = Meter.CreateCounter<long>(
        "knowledge_embedding_chunks_processed", unit: "chunks",
        description: "Total de chunks com embedding gerado");
    private static readonly Counter<long> ArticlesChunkedCounter = Meter.CreateCounter<long>(
        "knowledge_embedding_articles_chunked", unit: "articles",
        description: "Total de artigos re-chunkados");
    private static readonly Counter<long> ErrorsCounter = Meter.CreateCounter<long>(
        "knowledge_embedding_errors", unit: "errors",
        description: "Total de erros no ciclo de embedding");
    private static readonly Histogram<double> CycleDurationHistogram = Meter.CreateHistogram<double>(
        "knowledge_embedding_cycle_duration_ms", unit: "ms",
        description: "Duração do ciclo de embedding em milissegundos");

    // Workers unificados; DisallowConcurrentExecution do Quartz garante
    // que uma única instância executa por vez. O semáforo abaixo protege
    // contra calls de trigger manual enquanto um ciclo já está rodando.
    private static readonly SemaphoreSlim EmbeddingSemaphore = new(1, 1);

    public async Task Execute(IJobExecutionContext context)
    {
        if (!await EmbeddingSemaphore.WaitAsync(TimeSpan.Zero))
        {
            var logger = context.GetLogger<KnowledgeEmbeddingJob>();
            logger.LogInformation("Ciclo de embedding já em andamento — pulando esta execução.");
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await ProcessCycleAsync(context);
        }
        catch (Exception ex)
        {
            var logger = context.GetLogger<KnowledgeEmbeddingJob>();
            logger.LogError(ex, "Erro no ciclo de embedding da KB.");
            ErrorsCounter.Add(1);
        }
        finally
        {
            sw.Stop();
            CycleDurationHistogram.Record(sw.ElapsedMilliseconds);
            EmbeddingSemaphore.Release();
        }
    }

    private static async Task ProcessCycleAsync(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<KnowledgeEmbeddingJob>();

        await using var scope = scopeFactory.CreateAsyncScope();
        var articleRepository = scope.ServiceProvider.GetRequiredService<IKnowledgeArticleRepository>();
        var chunkRepository = scope.ServiceProvider.GetRequiredService<IKnowledgeChunkRepository>();
        var chunkingService = scope.ServiceProvider.GetRequiredService<IKnowledgeChunkingService>();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var resolver = scope.ServiceProvider.GetRequiredService<IConfigurationResolver>();
        var queueRepository = scope.ServiceProvider.GetRequiredService<IKnowledgeEmbeddingQueueRepository>();

        var aiSettings = await resolver.GetAISettingsAsync();
        var enabled = aiSettings.EmbeddingEnabled && aiSettings.EmbeddingArticlesEnabled;
        if (!enabled)
        {
            logger.LogDebug("Knowledge embedding desativado para este ciclo.");
            return;
        }

        // ── Passo 1: Processar fila LISTEN/NOTIFY ────────────────────────
        var queueProcessed = await ProcessQueueBatchAsync(
            scope, queueRepository, articleRepository, chunkRepository,
            chunkingService, embeddingProvider, aiSettings, logger);

        // ── Passo 2: Re-chunking ──────────────────────────────────────────
        var batchSize = 20;
        var articlesChunked = await ReChunkArticlesAsync(
            articleRepository, chunkRepository, chunkingService, batchSize, logger);

        // ── Passo 3: Geração de Embeddings ────────────────────────────────
        var chunksEmbedded = await GenerateEmbeddingsBatchAsync(
            scope, articleRepository, chunkRepository, embeddingProvider,
            aiSettings, batchSize, logger);

        if (queueProcessed || articlesChunked > 0 || chunksEmbedded > 0)
        {
            logger.LogInformation(
                "Ciclo KB concluído: queue={QueueProcessed}, chunked={ArticlesChunked}, embedded={ChunksEmbedded}",
                queueProcessed, articlesChunked, chunksEmbedded);
        }
    }

    private static async Task<bool> ProcessQueueBatchAsync(
        AsyncServiceScope scope,
        IKnowledgeEmbeddingQueueRepository queueRepository,
        IKnowledgeArticleRepository articleRepository,
        IKnowledgeChunkRepository chunkRepository,
        IKnowledgeChunkingService chunkingService,
        IEmbeddingProvider embeddingProvider,
        Discovery.Core.ValueObjects.AIIntegrationSettings aiSettings,
        ILogger logger)
    {
        var items = await queueRepository.ClaimBatchAsync(5);
        if (items.Count == 0)
            return false;

        foreach (var item in items)
        {
            try
            {
                var article = await articleRepository.GetByIdAsync(item.ArticleId);
                if (article == null || article.DeletedAt != null || !article.IsPublished)
                {
                    await queueRepository.MarkDoneAsync(item.Id);
                    continue;
                }

                var chunks = chunkingService.ChunkArticle(article);
                var inputs = chunks.Select(c =>
                    string.IsNullOrEmpty(c.SectionTitle)
                        ? c.Content
                        : $"{c.SectionTitle}\n\n{c.Content}").ToList();

                var (embBaseUrl, embApiKey) = await ResolveEmbeddingCredentialsAsync(
                    scope, article.ClientId, article.SiteId, aiSettings);

                var allEmbeddings = await embeddingProvider.GenerateEmbeddingsAsync(
                    inputs, aiSettings.EmbeddingModel, embApiKey, embBaseUrl);

                if (allEmbeddings.Count > 0 && allEmbeddings[0].Length != aiSettings.EmbeddingDimensions)
                {
                    logger.LogError(
                        "Dimensão do embedding ({Actual}) difere da configurada ({Expected}) para ArticleId={ArticleId}. Marcando erro na fila.",
                        allEmbeddings[0].Length, aiSettings.EmbeddingDimensions, article.Id);
                    await queueRepository.MarkFailedAsync(item.Id,
                        $"Embedding dimension mismatch: {allEmbeddings[0].Length} vs {aiSettings.EmbeddingDimensions}",
                        TimeSpan.FromMinutes(30));
                    continue;
                }

                for (int i = 0; i < chunks.Count && i < allEmbeddings.Count; i++)
                {
                    chunks[i].Embedding = new Vector(allEmbeddings[i]);
                    chunks[i].EmbeddingGeneratedAt = DateTime.UtcNow;
                }

                await chunkRepository.ReplaceAllForArticleAsync(article.Id, chunks);

                article.LastChunkedAt = DateTime.UtcNow;
                await articleRepository.UpdateAsync(article);

                await queueRepository.MarkDoneAsync(item.Id);
                ChunksProcessedCounter.Add(chunks.Count);
                ArticlesChunkedCounter.Add(1);
                logger.LogInformation(
                    "Fila KB processada via batch para ArticleId={ArticleId} ({ChunkCount} chunks).",
                    article.Id, chunks.Count);
            }
            catch (Exception ex)
            {
                var retryDelay = item.Attempts >= 5
                    ? TimeSpan.FromHours(1)
                    : TimeSpan.FromMinutes(2);

                await queueRepository.MarkFailedAsync(item.Id, ex.Message, retryDelay);
                ErrorsCounter.Add(1);
                logger.LogWarning(ex,
                    "Falha ao processar embedding da fila KB. ArticleId={ArticleId}, Attempts={Attempts}.",
                    item.ArticleId, item.Attempts);
            }
        }

        return true;
    }

    private static async Task<int> ReChunkArticlesAsync(
        IKnowledgeArticleRepository articleRepository,
        IKnowledgeChunkRepository chunkRepository,
        IKnowledgeChunkingService chunkingService,
        int batchSize,
        ILogger logger)
    {
        var articles = await articleRepository.GetArticlesNeedingChunkingAsync(batchSize);
        if (articles.Count == 0)
            return 0;

        logger.LogInformation("Re-chunking {Count} artigos...", articles.Count);

        foreach (var article in articles)
        {
            try
            {
                var chunks = chunkingService.ChunkArticle(article);
                await chunkRepository.ReplaceAllForArticleAsync(article.Id, chunks);

                article.LastChunkedAt = DateTime.UtcNow;
                await articleRepository.UpdateAsync(article);

                ArticlesChunkedCounter.Add(1);
                logger.LogDebug("Artigo {Id} chunkado em {Count} partes.", article.Id, chunks.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao chunkar artigo {Id}", article.Id);
                ErrorsCounter.Add(1);
            }
        }

        return articles.Count;
    }

    private static async Task<int> GenerateEmbeddingsBatchAsync(
        AsyncServiceScope scope,
        IKnowledgeArticleRepository articleRepository,
        IKnowledgeChunkRepository chunkRepository,
        IEmbeddingProvider embeddingProvider,
        Discovery.Core.ValueObjects.AIIntegrationSettings aiSettings,
        int batchSize,
        ILogger logger)
    {
        var chunks = await chunkRepository.GetChunksWithoutEmbeddingAsync(batchSize);
        if (chunks.Count == 0)
            return 0;

        logger.LogInformation("Gerando embeddings para {Count} chunks em batch...", chunks.Count);

        try
        {
            var inputs = chunks.Select(c =>
                string.IsNullOrEmpty(c.SectionTitle)
                    ? c.Content
                    : $"{c.SectionTitle}\n\n{c.Content}").ToList();

            var firstChunk = chunks[0];
            var article = await articleRepository.GetByIdAsync(firstChunk.ArticleId);

            var (embBaseUrl, embApiKey) = await ResolveEmbeddingCredentialsAsync(
                scope, article?.ClientId, article?.SiteId, aiSettings);

            var allEmbeddings = await embeddingProvider.GenerateEmbeddingsAsync(
                inputs, aiSettings.EmbeddingModel, embApiKey, embBaseUrl);

            if (allEmbeddings.Count > 0 && allEmbeddings[0].Length != aiSettings.EmbeddingDimensions)
            {
                logger.LogError(
                    "Dimensão do embedding ({Actual}) difere da configurada ({Expected}). Abortando batch.",
                    allEmbeddings[0].Length, aiSettings.EmbeddingDimensions);
                return 0;
            }

            for (int i = 0; i < chunks.Count && i < allEmbeddings.Count; i++)
            {
                await chunkRepository.UpdateEmbeddingAsync(chunks[i].Id, new Vector(allEmbeddings[i]));
            }

            ChunksProcessedCounter.Add(allEmbeddings.Count);
            logger.LogInformation("Embeddings batch concluído: {Count} chunks processados.", allEmbeddings.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro no batch de embeddings. Tentando fallback individual...");
            ErrorsCounter.Add(1);

            // Fallback individual
            foreach (var chunk in chunks)
            {
                try
                {
                    var input = string.IsNullOrEmpty(chunk.SectionTitle)
                        ? chunk.Content
                        : $"{chunk.SectionTitle}\n\n{chunk.Content}";

                    var embBaseUrl = string.IsNullOrWhiteSpace(aiSettings.EmbeddingBaseUrl)
                        ? aiSettings.BaseUrl
                        : aiSettings.EmbeddingBaseUrl;
                    var embApiKey = string.IsNullOrWhiteSpace(aiSettings.EmbeddingApiKey)
                        ? aiSettings.ApiKey
                        : aiSettings.EmbeddingApiKey;

                    var floats = await embeddingProvider.GenerateEmbeddingAsync(
                        input, aiSettings.EmbeddingModel, embApiKey, embBaseUrl);

                    await chunkRepository.UpdateEmbeddingAsync(chunk.Id, new Vector(floats));
                    ChunksProcessedCounter.Add(1);
                }
                catch (Exception inner)
                {
                    logger.LogError(inner, "Erro ao gerar embedding para chunk {Id}", chunk.Id);
                    ErrorsCounter.Add(1);
                }
            }
        }

        return chunks.Count;
    }

    private static async Task<(string? baseUrl, string? apiKey)> ResolveEmbeddingCredentialsAsync(
        AsyncServiceScope scope,
        Guid? clientId,
        Guid? siteId,
        Discovery.Core.ValueObjects.AIIntegrationSettings aiSettings)
    {
        if (clientId.HasValue || siteId.HasValue)
        {
            var credentialResolver = scope.ServiceProvider.GetRequiredService<IAiCredentialResolver>();
            var resolved = await credentialResolver.ResolveAsync(clientId, siteId);

            if (resolved is not null)
            {
                var baseUrl = resolved.EffectiveEmbeddingBaseUrl
                    ?? (string.IsNullOrWhiteSpace(aiSettings.EmbeddingBaseUrl) ? aiSettings.BaseUrl : aiSettings.EmbeddingBaseUrl);
                var apiKey = resolved.EffectiveEmbeddingApiKey
                    ?? (string.IsNullOrWhiteSpace(aiSettings.EmbeddingApiKey) ? aiSettings.ApiKey : aiSettings.EmbeddingApiKey);
                return (baseUrl, apiKey);
            }
        }

        // Fallback: usa configuração global de AI
        var embBaseUrl = string.IsNullOrWhiteSpace(aiSettings.EmbeddingBaseUrl)
            ? aiSettings.BaseUrl
            : aiSettings.EmbeddingBaseUrl;
        var embApiKey = string.IsNullOrWhiteSpace(aiSettings.EmbeddingApiKey)
            ? aiSettings.ApiKey
            : aiSettings.EmbeddingApiKey;

        return (embBaseUrl, embApiKey);
    }
}
