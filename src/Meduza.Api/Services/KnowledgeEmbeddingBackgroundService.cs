using Meduza.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Meduza.Api.Services;

/// <summary>
/// Background service que processa artigos da KB em dois passos a cada 30s:
/// 1. Re-chunking: artigos publicados com last_chunked_at desatualizado
/// 2. Embedding: chunks sem embedding gerado
/// Ativação controlada pelas configurações de IA persistidas em banco.
/// </summary>
public class KnowledgeEmbeddingBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<KnowledgeEmbeddingBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("KnowledgeEmbeddingBackgroundService iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no ciclo de embedding da KB. Próxima tentativa em {Interval}s", Interval.TotalSeconds);
            }

            await Task.Delay(Interval, stoppingToken);
        }

        logger.LogInformation("KnowledgeEmbeddingBackgroundService encerrado.");
    }

    private async Task ProcessCycleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
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
            return;
        }

        // ── Passo 1: Re-chunking ────────────────────────────────────────
        var articlesNeedingChunking = await articleRepository.GetArticlesNeedingChunkingAsync(BatchSize, ct);

        if (articlesNeedingChunking.Count > 0)
        {
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
        var chunksWithoutEmbedding = await chunkRepository.GetChunksWithoutEmbeddingAsync(BatchSize, ct);

        if (chunksWithoutEmbedding.Count > 0)
        {
            logger.LogInformation("Gerando embeddings para {Count} chunks...", chunksWithoutEmbedding.Count);

            foreach (var chunk in chunksWithoutEmbedding)
            {
                try
                {
                    // Input: título da seção + conteúdo (mais rico para embedding)
                    var embeddingInput = string.IsNullOrEmpty(chunk.SectionTitle)
                        ? chunk.Content
                        : $"{chunk.SectionTitle}\n\n{chunk.Content}";

                    var floats = await embeddingProvider.GenerateEmbeddingAsync(embeddingInput, aiSettings.EmbeddingModel, aiSettings.ApiKey, ct);
                    await chunkRepository.UpdateEmbeddingAsync(chunk.Id, new Vector(floats), ct);

                    logger.LogDebug("Embedding gerado para chunk {Id} ({Tokens} tokens)", chunk.Id, chunk.TokenCount);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erro ao gerar embedding para chunk {Id}", chunk.Id);
                }
            }
        }
    }
}
