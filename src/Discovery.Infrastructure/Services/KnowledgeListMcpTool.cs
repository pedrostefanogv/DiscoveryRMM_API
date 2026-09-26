using System.Text.Json;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação da MCP Tool "knowledge_list".
/// Retorna o catálogo de artigos PUBLICADOS no escopo (site → client → global),
/// com metadados leves (título, categoria, escopo, tags, resumo) em vez do
/// conteúdo completo. Não exige parâmetros — é a resposta para perguntas de
/// catálogo ("quais artigos existem?", "o que tem na base?").
/// </summary>
public class KnowledgeListMcpTool(
    IKnowledgeArticleRepository articleRepository,
    ILogger<KnowledgeListMcpTool> logger) : IKnowledgeListMcpTool
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;
    private const int SummaryMaxChars = 180;

    public async Task<string> ExecuteAsync(
        Guid? clientId,
        Guid? siteId,
        string? category,
        int limit,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();

        logger.LogDebug(
            "KnowledgeListMcpTool.Execute: clientId={ClientId}, siteId={SiteId}, category={Category}, limit={Limit}",
            clientId, siteId, normalizedCategory, clamped);

        try
        {
            // Mesma regra de escopo do endpoint agent-auth (GetKnowledgeArticlesHandler):
            // status "Published" e herança site → client → global. Artigos Internal
            // NÃO são expostos ao chat (orientação interna do departamento).
            var data = await articleRepository.ListByUserScopeAsync(
                hasGlobalAccess: false,
                allowedClientIds: new HashSet<Guid>(),
                allowedSiteIds: new HashSet<Guid>(),
                status: "Published",
                departmentId: null,
                category: normalizedCategory,
                cursor: null,
                limit: clamped,
                filterClientId: clientId,
                filterSiteId: siteId,
                sortBy: "updatedAt",
                sortDirection: "desc",
                ct: ct);

            var total = await articleRepository.CountPublishedAsync(clientId, siteId, ct);

            var articles = data.Items.Select(a => new
            {
                article_id = a.Id,
                title = a.Title,
                category = a.Category,
                scope = GetScope(a.ClientId, a.SiteId),
                tags = ParseTags(a.TagsJson),
                summary = BuildSummary(a.Content),
                updated_at = a.UpdatedAt,
                internal_url = $"discovery://knowledge/article/{a.Id}"
            }).ToList();

            var message = total == 0
                ? "A base de conhecimento ainda não tem artigos publicados para este escopo."
                : data.HasMore
                    ? $"Existem mais artigos além dos {articles.Count} listados. Refine com o parâmetro category."
                    : null;

            return JsonSerializer.Serialize(new
            {
                found = true,
                count = articles.Count,
                total,
                has_more = data.HasMore,
                message,
                articles
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao listar artigos da base de conhecimento (knowledge_list)");
            return JsonSerializer.Serialize(new
            {
                found = false,
                error = "Erro ao consultar a base de conhecimento."
            });
        }
    }

    private static string GetScope(Guid? clientId, Guid? siteId) =>
        (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };

    private static List<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(tagsJson) ?? []; }
        catch { return []; }
    }

    /// <summary>Primeira linha não vazia do conteúdo, sem marcação, truncada por runas.</summary>
    private static string BuildSummary(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '*', '-', ' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.').Trim();
            if (line.Length == 0) continue;
            return line.Length > SummaryMaxChars ? line[..SummaryMaxChars] + "..." : line;
        }
        return string.Empty;
    }
}
