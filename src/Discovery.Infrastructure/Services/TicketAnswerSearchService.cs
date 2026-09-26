using System.Diagnostics;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Helpers;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Busca por resposta do questionário. Modo semântico: embedding da consulta +
/// pgvector (cosine distance), deduplicado por chamado (melhor resposta).
/// Degrada automaticamente para correspondência por texto quando o provedor
/// está indisponível, não há embeddings ou nada atinge o corte de similaridade
/// (mesmo desenho do <see cref="KnowledgeSearchService"/> híbrido).
/// </summary>
public class TicketAnswerSearchService(
    ITicketAnswerRepository repository,
    IEmbeddingProvider embeddingProvider,
    IConfigurationResolver configurationResolver,
    IAiCredentialResolver credentialResolver,
    IScopeContext scopeContext,
    ILogger<TicketAnswerSearchService> logger) : ITicketAnswerSearchService
{
    public async Task<TicketAnswerSearchResult> SearchAsync(
        TicketAnswerSearchRequest request,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var query = (request.Query ?? string.Empty).Trim();
        if (query.Length == 0)
            return new TicketAnswerSearchResult("keyword", Array.Empty<TicketAnswerSearchHit>(), sw.ElapsedMilliseconds);

        var max = Math.Clamp(request.Limit <= 0 ? 10 : request.Limit, 1, 50);

        // ACL sempre resolvida aqui (nunca aceita escopo do chamador).
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);

        var settings = await configurationResolver.GetAISettingsAsync();
        var semanticEnabled = settings.EmbeddingEnabled && settings.EmbeddingTicketAnswersEnabled;

        if (semanticEnabled)
        {
            try
            {
                // Escopo global: a busca cruza clientes permitidos pelo ACL.
                var credential = await credentialResolver.ResolveAsync(null, null, ct);
                var baseUrl = credential?.EffectiveEmbeddingBaseUrl
                    ?? (string.IsNullOrWhiteSpace(settings.EmbeddingBaseUrl) ? settings.BaseUrl : settings.EmbeddingBaseUrl);
                var apiKey = credential?.EffectiveEmbeddingApiKey
                    ?? (string.IsNullOrWhiteSpace(settings.EmbeddingApiKey) ? settings.ApiKey : settings.EmbeddingApiKey);

                var embedding = await embeddingProvider.GenerateEmbeddingAsync(
                    query, settings.EmbeddingModel, apiKey, baseUrl, ct);

                var hits = await repository.SearchSemanticAsync(
                    new Vector(embedding),
                    access.HasGlobalAccess,
                    access.AllowedClientIds,
                    access.AllowedSiteIds,
                    request.TemplateId,
                    request.QuestionKey,
                    max,
                    request.MinSimilarity ?? settings.MinSimilarityScore,
                    ct);

                var deduped = DedupeByTicket(hits).Take(max).ToList();
                if (deduped.Count > 0)
                    return new TicketAnswerSearchResult("semantic", deduped, sw.ElapsedMilliseconds);

                logger.LogInformation(
                    "[TicketAnswerSearch] Semântico sem resultados — usando fallback por texto. Query={Query}",
                    LogSanitizer.Sanitize(query));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[TicketAnswerSearch] Busca semântica indisponível — usando fallback por texto. Query={Query}",
                    LogSanitizer.Sanitize(query));
            }
        }

        var keywordHits = await repository.SearchKeywordAsync(
            query,
            access.HasGlobalAccess,
            access.AllowedClientIds,
            access.AllowedSiteIds,
            request.TemplateId,
            request.QuestionKey,
            max,
            ct);

        var mode = semanticEnabled ? "keyword" : "disabled";
        return new TicketAnswerSearchResult(mode, keywordHits, sw.ElapsedMilliseconds);
    }

    /// <summary>Mantém a melhor resposta por chamado (menor distância).</summary>
    private static IEnumerable<TicketAnswerSearchHit> DedupeByTicket(IReadOnlyList<TicketAnswerSearchHit> hits)
    {
        var best = new Dictionary<Guid, TicketAnswerSearchHit>();
        foreach (var hit in hits)
        {
            if (!best.TryGetValue(hit.TicketId, out var current) || hit.Distance < current.Distance)
                best[hit.TicketId] = hit;
        }

        return best.Values.OrderBy(h => h.Distance);
    }
}
