using System.Text.Json;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Implementação da MCP Tool "knowledge_search".
/// Executa busca semântica + keyword na KB e retorna JSON para o LLM.
/// Registrada como tool call quando EnableTools=true no AiChatService.
/// </summary>
public class KnowledgeMcpTool(
    IKnowledgeChunkRepository chunkRepository,
    IKnowledgeArticleRepository articleRepository,
    IEmbeddingProvider embeddingProvider,
    IConfigurationResolver configurationResolver,
    ILogger<KnowledgeMcpTool> logger) : IKnowledgeMcpTool
{
    public Task<string> ExecuteAsync(
        Guid? clientId,
        Guid? siteId,
        string query,
        int maxResults = 3,
        CancellationToken ct = default)
        => ExecuteInternalAsync(clientId, siteId, query, aiSettings: null, excludeArticleIds: null, maxResults, ct);

    public Task<string> ExecuteWithSettingsAsync(
        Guid? clientId,
        Guid? siteId,
        string query,
        AIIntegrationSettings aiSettings,
        IReadOnlyCollection<Guid>? excludeArticleIds = null,
        int maxResults = 3,
        CancellationToken ct = default)
        => ExecuteInternalAsync(clientId, siteId, query, aiSettings, excludeArticleIds, maxResults, ct);

    private async Task<string> ExecuteInternalAsync(
        Guid? clientId,
        Guid? siteId,
        string query,
        AIIntegrationSettings? aiSettings,
        IReadOnlyCollection<Guid>? excludeArticleIds,
        int maxResults,
        CancellationToken ct)
    {
        logger.LogDebug(
            "KnowledgeMcpTool.ExecuteInternalAsync: query={Query}, clientId={ClientId}, siteId={SiteId}, max={Max}",
            query, clientId, siteId, maxResults);

        try
        {
            // Usa settings passadas pelo caller (evita GetAISettingsAsync redundante por tool call)
            var settings = aiSettings ?? await configurationResolver.GetAISettingsAsync();

            // Busca semântica via embedding
            var embedding = await embeddingProvider.GenerateEmbeddingAsync(
                query,
                settings.EmbeddingModel,
                settings.ApiKey,
                ct);
            var vector = new Vector(embedding);

            var semanticResults = await chunkRepository.SearchSemanticAsync(
                vector, clientId, siteId, maxResults,
                minSimilarity: settings.MinSimilarityScore,
                excludeArticleIds: excludeArticleIds,
                ct);

            if (semanticResults.Count == 0)
            {
                // Fallback: busca keyword
                var keywordResults = await articleRepository.SearchKeywordAsync(query, clientId, siteId, ct);
                if (keywordResults.Count == 0)
                    return JsonSerializer.Serialize(new { found = false, message = "Nenhum artigo encontrado na base de conhecimento." });

                var kwItems = keywordResults.Take(maxResults).Select(a => new
                {
                    article_id = a.Id,
                    title = a.Title,
                    section = (string?)null,
                    content = a.Content.Length > 600 ? a.Content[..600] + "..." : a.Content,
                    score = (double?)null,
                    scope = GetScope(a.ClientId, a.SiteId)
                });

                return JsonSerializer.Serialize(new { found = true, results = kwItems });
            }

            var items = semanticResults.Select(r => new
            {
                article_id = r.ArticleId,
                title = r.ArticleTitle,
                section = r.SectionTitle,
                content = r.ChunkContent.Length > 600 ? r.ChunkContent[..600] + "..." : r.ChunkContent,
                score = Math.Round(1.0 - r.Distance, 4), // converte distância para similaridade
                scope = GetScope(r.ArticleClientId, r.ArticleSiteId)
            });

            return JsonSerializer.Serialize(new { found = true, results = items });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao executar KnowledgeMcpTool para query={Query}", query);
            return JsonSerializer.Serialize(new { found = false, error = "Erro ao consultar base de conhecimento." });
        }
    }

    private static string GetScope(Guid? clientId, Guid? siteId) =>
        (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };
}
