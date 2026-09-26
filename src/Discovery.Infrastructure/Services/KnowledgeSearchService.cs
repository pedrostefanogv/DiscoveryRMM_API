using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.ValueObjects;
using Discovery.Core.Helpers;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Orquestra a busca na Knowledge Base (semantic / keyword / hybrid).
/// Modo semantic: embedding da query + pgvector (cosine distance), resultados
/// deduplicados por artigo (melhor chunk). Modo hybrid: cai para keyword quando
/// o motor de IA está indisponível ou não encontra nada. Modo keyword: ILIKE.
/// </summary>
public class KnowledgeSearchService(
    IKnowledgeChunkRepository chunkRepository,
    IKnowledgeArticleRepository articleRepository,
    IEmbeddingProvider embeddingProvider,
    IConfigurationResolver configurationResolver,
    IAiCredentialResolver credentialResolver,
    IScopeContext scopeContext,
    ILogger<KnowledgeSearchService> logger) : IKnowledgeSearchService
{
    public async Task<List<KnowledgeSearchHit>> SearchAsync(KnowledgeSearchRequest request, CancellationToken ct = default)
    {
        var query = (request.Query ?? string.Empty).Trim();
        if (query.Length == 0) return [];

        var max = Math.Clamp(request.MaxResults <= 0 ? 10 : request.MaxResults, 1, 50);
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "hybrid" : request.Mode.Trim().ToLowerInvariant();

        // Resolva o escopo: ACL do usuário (multi-escopo) ou legado clientId/siteId.
        bool hasGlobal = false;
        var allowedClients = new HashSet<Guid>();
        var allowedSites = new HashSet<Guid>();
        if (request.UseUserScope)
        {
            var scope = await scopeContext.GetAccessAsync(ResourceType.KnowledgeBase, ActionType.View);
            hasGlobal = scope.HasGlobalAccess;
            allowedClients = scope.AllowedClientIds.ToHashSet();
            allowedSites = scope.AllowedSiteIds.ToHashSet();
        }

        // ── Semântico (semantic e hybrid) ──
        if (mode is "semantic" or "hybrid")
        {
            var semanticHits = await TrySemanticAsync(query, request, max, hasGlobal, allowedClients, allowedSites, ct);
            if (semanticHits.Count > 0) return semanticHits;

            if (mode == "semantic") return []; // semântico puro: sem fallback
            logger.LogInformation(
                "[KnowledgeSearch] Semântico sem resultados/indisponível — fallback para keyword. Query={Query}",
                LogSanitizer.Sanitize(query));
        }

        // ── Keyword (ou fallback do hybrid) ──
        var articles = request.UseUserScope
            ? await articleRepository.SearchKeywordByUserScopeAsync(
                query, hasGlobal, allowedClients, allowedSites, request.DepartmentId, publishedOnly: false, ct)
            : await articleRepository.SearchKeywordAsync(query, request.ClientId, request.SiteId, request.DepartmentId, publishedOnly: false, ct);

        return articles.Take(max)
            .Select(a => new KnowledgeSearchHit(a, null, null, "keyword"))
            .ToList();
    }

    private async Task<List<KnowledgeSearchHit>> TrySemanticAsync(
        string query,
        KnowledgeSearchRequest request,
        int max,
        bool hasGlobal,
        HashSet<Guid> allowedClients,
        HashSet<Guid> allowedSites,
        CancellationToken ct)
    {
        try
        {
            var settings = await configurationResolver.GetAISettingsAsync();

            // Credencial por escopo (Site > Client > Global) quando há escopo legado
            if (request.ClientId.HasValue || request.SiteId.HasValue)
            {
                var credential = await credentialResolver.ResolveAsync(request.ClientId, request.SiteId, ct);
                if (credential is not null)
                {
                    if (!string.IsNullOrWhiteSpace(credential.EffectiveEmbeddingApiKey))
                        settings.EmbeddingApiKey = credential.EffectiveEmbeddingApiKey;
                    if (!string.IsNullOrWhiteSpace(credential.EffectiveEmbeddingBaseUrl))
                        settings.EmbeddingBaseUrl = credential.EffectiveEmbeddingBaseUrl;
                }
            }

            var embBaseUrl = string.IsNullOrWhiteSpace(settings.EmbeddingBaseUrl) ? settings.BaseUrl : settings.EmbeddingBaseUrl;
            var embApiKey = string.IsNullOrWhiteSpace(settings.EmbeddingApiKey) ? settings.ApiKey : settings.EmbeddingApiKey;
            var embedding = await embeddingProvider.GenerateEmbeddingAsync(query, settings.EmbeddingModel, embApiKey, embBaseUrl, ct);
            var vector = new Vector(embedding);

            var chunks = request.UseUserScope
                ? await chunkRepository.SearchSemanticByUserScopeAsync(
                    vector, hasGlobal, allowedClients, allowedSites,
                    max, settings.MinSimilarityScore, excludeArticleIds: null, request.DepartmentId, publishedOnly: false, ct)
                : await chunkRepository.SearchSemanticAsync(
                    vector, request.ClientId, request.SiteId,
                    max, settings.MinSimilarityScore, excludeArticleIds: null, request.DepartmentId, publishedOnly: false, ct);

            if (chunks.Count == 0) return [];

            // Dedupe por artigo (melhor chunk = menor distância) e resolve a entidade completa
            var bestByArticle = new Dictionary<Guid, KnowledgeChunkSearchResult>();
            foreach (var chunk in chunks)
            {
                if (!bestByArticle.TryGetValue(chunk.ArticleId, out var current) || chunk.Distance < current.Distance)
                    bestByArticle[chunk.ArticleId] = chunk;
            }

            var hits = new List<KnowledgeSearchHit>(bestByArticle.Count);
            foreach (var chunk in bestByArticle.Values)
            {
                var article = await articleRepository.GetByIdAsync(chunk.ArticleId, ct);
                if (article is null || article.DeletedAt != null) continue;
                hits.Add(new KnowledgeSearchHit(article, chunk, 1.0 - chunk.Distance, "semantic"));
            }

            return hits;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[KnowledgeSearch] Falha na busca semântica (motor de IA/embedding indisponível). Query={Query}",
                LogSanitizer.Sanitize(query));
            return [];
        }
    }
}
