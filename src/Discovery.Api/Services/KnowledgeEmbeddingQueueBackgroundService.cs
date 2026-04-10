using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;

namespace Discovery.Api.Services;

public class KnowledgeEmbeddingQueueBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<KnowledgeEmbeddingQueueBackgroundService> logger) : BackgroundService
{
    private const string NotificationChannel = "knowledge_embedding_queue";

    private readonly TimeSpan _startupDelay = TimeSpan.FromSeconds(Math.Max(0,
        configuration.GetValue<int?>("BackgroundJobs:KnowledgeEmbeddingQueue:StartupDelaySeconds") ?? 5));
    private readonly TimeSpan _idleInterval = TimeSpan.FromSeconds(Math.Max(30,
        configuration.GetValue<int?>("BackgroundJobs:KnowledgeEmbeddingQueue:IdleIntervalSeconds") ?? 600));
    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(Math.Max(10,
        configuration.GetValue<int?>("BackgroundJobs:KnowledgeEmbeddingQueue:RetryDelaySeconds") ?? 120));
    private readonly int _batchSize = Math.Max(1,
        configuration.GetValue<int?>("BackgroundJobs:KnowledgeEmbeddingQueue:BatchSize") ?? 5);
    private readonly int _maxAttempts = Math.Max(1,
        configuration.GetValue<int?>("BackgroundJobs:KnowledgeEmbeddingQueue:MaxAttempts") ?? 5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("KnowledgeEmbeddingQueueBackgroundService iniciado.");
        await Task.Delay(_startupDelay, stoppingToken);

        await using var listenConnection = await OpenListenConnectionAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedAny = await ProcessBatchAsync(stoppingToken);
                if (processedAny)
                    continue;

                await WaitForNotificationOrTimeoutAsync(listenConnection, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no worker de fila de embeddings da KB.");
                await Task.Delay(_idleInterval, stoppingToken);
            }
        }

        logger.LogInformation("KnowledgeEmbeddingQueueBackgroundService encerrado.");
    }

    private async Task<bool> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var queueRepository = scope.ServiceProvider.GetRequiredService<IKnowledgeEmbeddingQueueRepository>();
        var articleRepository = scope.ServiceProvider.GetRequiredService<IKnowledgeArticleRepository>();
        var chunkRepository = scope.ServiceProvider.GetRequiredService<IKnowledgeChunkRepository>();
        var chunkingService = scope.ServiceProvider.GetRequiredService<IKnowledgeChunkingService>();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var configurationResolver = scope.ServiceProvider.GetRequiredService<IConfigurationResolver>();

        var aiSettings = await configurationResolver.GetAISettingsAsync();
        var enabled = aiSettings.EmbeddingEnabled && aiSettings.EmbeddingArticlesEnabled;
        if (!enabled)
        {
            logger.LogDebug("Knowledge embedding desativado nas configurações de IA.");
            return false;
        }

        var items = await queueRepository.ClaimBatchAsync(_batchSize, ct);
        if (items.Count == 0)
            return false;

        foreach (var item in items)
        {
            try
            {
                var article = await articleRepository.GetByIdAsync(item.ArticleId, ct);
                if (article == null || article.DeletedAt != null || !article.IsPublished)
                {
                    await queueRepository.MarkDoneAsync(item.Id, ct);
                    continue;
                }

                var chunks = chunkingService.ChunkArticle(article);
                foreach (var chunk in chunks)
                {
                    var embeddingInput = string.IsNullOrEmpty(chunk.SectionTitle)
                        ? chunk.Content
                        : $"{chunk.SectionTitle}\n\n{chunk.Content}";

                    var embBaseUrl = string.IsNullOrWhiteSpace(aiSettings.EmbeddingBaseUrl) ? aiSettings.BaseUrl : aiSettings.EmbeddingBaseUrl;
                    var embApiKey = string.IsNullOrWhiteSpace(aiSettings.EmbeddingApiKey) ? aiSettings.ApiKey : aiSettings.EmbeddingApiKey;
                    var floats = await embeddingProvider.GenerateEmbeddingAsync(
                        embeddingInput,
                        aiSettings.EmbeddingModel,
                        embApiKey,
                        embBaseUrl,
                        ct);

                    chunk.Embedding = new Vector(floats);
                    chunk.EmbeddingGeneratedAt = DateTime.UtcNow;
                }

                await chunkRepository.ReplaceAllForArticleAsync(article.Id, chunks, ct);
                article.LastChunkedAt = DateTime.UtcNow;
                await articleRepository.UpdateAsync(article, ct);

                await queueRepository.MarkDoneAsync(item.Id, ct);
                logger.LogInformation("Embeddings da KB atualizados para ArticleId={ArticleId}.", article.Id);
            }
            catch (Exception ex)
            {
                var retryDelay = item.Attempts >= _maxAttempts
                    ? TimeSpan.FromHours(1)
                    : _retryDelay;

                await queueRepository.MarkFailedAsync(item.Id, ex.Message, retryDelay, ct);
                logger.LogWarning(ex,
                    "Falha ao processar embedding da KB. ArticleId={ArticleId}, Attempts={Attempts}.",
                    item.ArticleId,
                    item.Attempts);
            }
        }

        return true;
    }

    private async Task<NpgsqlConnection> OpenListenConnectionAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand($"LISTEN {NotificationChannel};", connection);
        await command.ExecuteNonQueryAsync(ct);

        return connection;
    }

    private async Task WaitForNotificationOrTimeoutAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var waitTask = connection.WaitAsync(waitCts.Token);
        var delayTask = Task.Delay(_idleInterval, ct);

        var completed = await Task.WhenAny(waitTask, delayTask);
        if (completed == waitTask)
        {
            await waitTask;
            return;
        }

        waitCts.Cancel();
        try
        {
            await waitTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when timeout or shutdown occurs.
        }
    }
}
