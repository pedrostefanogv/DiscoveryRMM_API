using System.Text.Json;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Discovery.Infrastructure.Services;

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
    IAiCredentialResolver credentialResolver,
    ILogger<KnowledgeMcpTool> logger) : IKnowledgeMcpTool
{
    public Task<string> ExecuteAsync(
        Guid? clientId,
        Guid? siteId,
        string query,
        int maxResults = 3,
        Guid? departmentId = null,
        CancellationToken ct = default)
        => ExecuteInternalAsync(clientId, siteId, query, aiSettings: null, excludeArticleIds: null, maxResults, departmentId, ct);

    public Task<string> ExecuteWithSettingsAsync(
        Guid? clientId,
        Guid? siteId,
        string query,
        AIIntegrationSettings aiSettings,
        IReadOnlyCollection<Guid>? excludeArticleIds = null,
        int maxResults = 3,
        Guid? departmentId = null,
        CancellationToken ct = default)
        => ExecuteInternalAsync(clientId, siteId, query, aiSettings, excludeArticleIds, maxResults, departmentId, ct);

    private async Task<string> ExecuteInternalAsync(
        Guid? clientId,
        Guid? siteId,
        string query,
        AIIntegrationSettings? aiSettings,
        IReadOnlyCollection<Guid>? excludeArticleIds,
        int maxResults,
        Guid? departmentId,
        CancellationToken ct)
    {
        logger.LogDebug(
            "KnowledgeMcpTool.ExecuteInternalAsync: query={Query}, clientId={ClientId}, siteId={SiteId}, deptId={DeptId}, max={Max}",
            LogSanitizer.Sanitize(query), clientId, siteId, departmentId, maxResults);

        // Configuração (compartilhada entre caminhos) — não mutar o objeto do chamador
        var settings = aiSettings ?? await configurationResolver.GetAISettingsAsync();
        var embBaseUrl = string.IsNullOrWhiteSpace(settings.EmbeddingBaseUrl) ? settings.BaseUrl : settings.EmbeddingBaseUrl;
        var embApiKey = string.IsNullOrWhiteSpace(settings.EmbeddingApiKey) ? settings.ApiKey : settings.EmbeddingApiKey;

        // Sobrescreve com credencial por escopo (Site > Client > Global) se disponível
        if (clientId.HasValue || siteId.HasValue)
        {
            var credential = await credentialResolver.ResolveAsync(clientId, siteId, ct);
            if (credential is not null)
            {
                if (!string.IsNullOrWhiteSpace(credential.EffectiveEmbeddingApiKey))
                    embApiKey = credential.EffectiveEmbeddingApiKey;
                if (!string.IsNullOrWhiteSpace(credential.EffectiveEmbeddingBaseUrl))
                    embBaseUrl = credential.EffectiveEmbeddingBaseUrl;
            }
        }

        List<KnowledgeChunkSearchResult>? semanticResults = null;

        // ── Tentativa de busca híbrida (semântica + keyword com RRF) ──
        try
        {
            var embedding = await embeddingProvider.GenerateEmbeddingAsync(
                query,
                settings.EmbeddingModel,
                embApiKey,
                embBaseUrl,
                ct);
            var vector = new Vector(embedding);

            semanticResults = await chunkRepository.SearchSemanticAsync(
                vector, clientId, siteId, Math.Max(maxResults * 2, maxResults),
                minSimilarity: settings.MinSimilarityScore,
                excludeArticleIds: excludeArticleIds,
                departmentId: departmentId,
                publishedOnly: true,
                ct: ct,
                forAi: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[KnowledgeMcpTool] Falha na busca semântica (motor de IA/embedding indisponível). " +
                "Fallback automático para keyword. Query={Query}",
                LogSanitizer.Sanitize(query));
        }

        List<Discovery.Core.Entities.KnowledgeArticle> keywordResults = [];
        try
        {
            keywordResults = await articleRepository.SearchKeywordAsync(
                query, clientId, siteId, departmentId: departmentId, publishedOnly: true, ct, forAi: true,
                excludeArticleIds: excludeArticleIds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[KnowledgeMcpTool] Falha na busca keyword para query={Query}", LogSanitizer.Sanitize(query));
        }

        // ── Fusão RRF (Reciprocal Rank Fusion) quando há resultados semânticos e/ou keyword ──
        if (semanticResults is { Count: > 0 } || keywordResults.Count > 0)
        {
            const double rrfK = 60.0;
            var scores = new Dictionary<Guid, double>();
            var bestChunkByArticle = new Dictionary<Guid, List<KnowledgeChunkSearchResult>>();
            var kwByArticle = new Dictionary<Guid, Discovery.Core.Entities.KnowledgeArticle>();

            if (semanticResults is not null)
            {
                for (var i = 0; i < semanticResults.Count; i++)
                {
                    var r = semanticResults[i];
                    scores[r.ArticleId] = scores.GetValueOrDefault(r.ArticleId) + (1.0 / (rrfK + i + 1));
                    if (!bestChunkByArticle.TryGetValue(r.ArticleId, out var list))
                    {
                        list = [];
                        bestChunkByArticle[r.ArticleId] = list;
                    }
                    if (list.Count < 2) list.Add(r);
                }
            }

            for (var i = 0; i < keywordResults.Count; i++)
            {
                var a = keywordResults[i];
                scores[a.Id] = scores.GetValueOrDefault(a.Id) + (0.8 / (rrfK + i + 1));
                kwByArticle.TryAdd(a.Id, a);
            }

            var fusedItems = new List<object>(maxResults);
            foreach (var (articleId, _) in scores.OrderByDescending(kv => kv.Value))
            {
                if (fusedItems.Count >= maxResults) break;
                if (bestChunkByArticle.TryGetValue(articleId, out var chunks))
                {
                    foreach (var chunk in chunks)
                    {
                        if (fusedItems.Count >= maxResults) break;
                        fusedItems.Add(BuildCitationResult(chunk, null, settings));
                    }
                }
                else if (kwByArticle.TryGetValue(articleId, out var art))
                {
                    fusedItems.Add(BuildKeywordCitation(art, settings));
                }
            }

            return JsonSerializer.Serialize(new { found = true, results = fusedItems });
        }

        return await BuildEmptyResultAsync(clientId, siteId, ct);
    }

    /// <summary>
    /// Resultado de busca sem match. Distingue "base sem artigos publicados" de
    /// "nenhum artigo corresponde à busca" para o LLM não afirmar que a base
    /// está vazia quando na verdade faltou correspondência.
    /// </summary>
    private async Task<string> BuildEmptyResultAsync(Guid? clientId, Guid? siteId, CancellationToken ct)
    {
        bool hasArticles;
        try
        {
            hasArticles = await articleRepository.HasPublishedArticlesAsync(clientId, siteId, ct, forAi: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao verificar artigos publicados da base de conhecimento");
            hasArticles = false;
        }

        return JsonSerializer.Serialize(new
        {
            found = false,
            has_articles_in_scope = hasArticles,
            message = hasArticles
                ? "Nenhum artigo corresponde à busca. Se a pergunta for sobre o catálogo de artigos, use knowledge_list."
                : "A base de conhecimento não possui artigos publicados para este escopo."
        });
    }

    private static object BuildCitationResult(KnowledgeChunkSearchResult r, string? keywordScore, AIIntegrationSettings settings)
    {
        if (string.Equals(r.ArticleStatus, Discovery.Core.Enums.ArticleStatus.AiOnly.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                ai_only = true,
                content = TruncateContent(r.ChunkContent),
                score = keywordScore ?? Math.Round(1.0 - r.Distance, 4).ToString("F4"),
                scope = GetScope(r.ArticleClientId, r.ArticleSiteId),
                source_line = keywordScore is not null ? "keyword" : "semantic",
                guidance = "Use este conteúdo apenas para orientar a resposta; nunca cite o título nem gere link clicável."
            };
        }

        var baseFields = new
        {
            article_id = r.ArticleId,
            title = r.ArticleTitle,
            section = r.SectionTitle,
            content = TruncateContent(r.ChunkContent),
            score = keywordScore ?? Math.Round(1.0 - r.Distance, 4).ToString("F4"),
            scope = GetScope(r.ArticleClientId, r.ArticleSiteId),
            source_line = keywordScore is not null ? "keyword" : "semantic",
            internal_url = BuildArticleUrl(r.ArticleId)
        };

        if (!settings.CitationsEnabled)
            return baseFields;

        // Citações enriquecidas: inclui metadados para o LLM citar fontes
        return new
        {
            baseFields.article_id,
            baseFields.title,
            baseFields.section,
            baseFields.content,
            baseFields.score,
            baseFields.scope,
            baseFields.source_line,
            baseFields.internal_url,
            citation = FormattableString.Invariant($"[Fonte: {r.ArticleTitle}]")
        };
    }

    private static object BuildKeywordCitation(Discovery.Core.Entities.KnowledgeArticle a, AIIntegrationSettings settings)
    {
        if (string.Equals(a.Status, Discovery.Core.Enums.ArticleStatus.AiOnly.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                ai_only = true,
                content = TruncateContent(a.Content),
                score = (string?)null,
                scope = GetScope(a.ClientId, a.SiteId),
                source_line = "keyword",
                guidance = "Use este conteúdo apenas para orientar a resposta; nunca cite o título nem gere link clicável."
            };
        }

        if (!settings.CitationsEnabled)
        {
            return new
            {
                article_id = a.Id,
                title = a.Title,
                section = (string?)null,
                content = TruncateContent(a.Content),
                score = (string?)null,
                scope = GetScope(a.ClientId, a.SiteId),
                source_line = "keyword",
                internal_url = BuildArticleUrl(a.Id)
            };
        }

        return new
        {
            article_id = a.Id,
            title = a.Title,
            section = (string?)null,
            content = TruncateContent(a.Content),
            score = (string?)null,
            scope = GetScope(a.ClientId, a.SiteId),
            source_line = "keyword",
            internal_url = BuildArticleUrl(a.Id),
            citation = FormattableString.Invariant($"[Fonte: {a.Title}]")
        };
    }

    /// <summary>
    /// Trecho entregue ao LLM: sem os tokens de imagem (![alt](kbimage:...)).
    /// O modelo não deve receber/repetir esse identificador nem prometer abrir
    /// uma imagem que ele não consegue ver (a visão fica fora do escopo desta
    /// fase — a imagem é exibida no app, não pelo LLM).
    /// </summary>
    private static string TruncateContent(string? content)
    {
        var plain = KnowledgeImageMarkdown.StripTokens(content);
        return plain.Length > 1600 ? plain[..1600] + "..." : plain;
    }

    /// <summary>Deep link interno que abre o artigo direto no app (card do chat).</summary>
    private static string BuildArticleUrl(Guid articleId) => $"discovery://knowledge/article/{articleId}";

    private static string GetScope(Guid? clientId, Guid? siteId) =>
        (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };
}
