using Discovery.Core.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Knowledge base endpoints for agents.
/// </summary>
public partial class AgentAuthController
{
    [HttpGet("knowledge")]
    public async Task<IActionResult> GetKnowledgeArticles(CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var site = await _siteRepo.GetByIdAsync(agent!.SiteId);
        var articles = await _knowledgeRepo.ListByScopeAsync(
            clientId: site?.ClientId,
            siteId: agent.SiteId,
            status: "Published",
            category: null,
            ct: ct);

        // Mapeia para DTOs planos para evitar recursão infinita na serialização JSON
        // (Article → Chunks → Article → Chunks → ...)
        var dtos = articles.Select(a => new AgentKnowledgeArticleDto(
            a.Id,
            a.Title,
            a.Content,
            a.Category,
            ParseTags(a.TagsJson),
            a.Status,
            a.CurrentVersionNumber,
            a.PublishedAt,
            a.LastEditedAt)).ToList();

        return Ok(dtos);
    }

    [HttpGet("knowledge/{articleId:guid}")]
    public async Task<IActionResult> GetKnowledgeArticle(Guid articleId, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var article = await _knowledgeRepo.GetByIdAsync(articleId, ct);
        if (article is null)
            return NotFound(new { error = "Article not found." });

        // Mapeia para DTO plano para evitar recursão infinita
        var dto = new AgentKnowledgeArticleDto(
            article.Id,
            article.Title,
            article.Content,
            article.Category,
            ParseTags(article.TagsJson),
            article.Status,
            article.CurrentVersionNumber,
            article.PublishedAt,
            article.LastEditedAt);

        return Ok(dto);
    }

    private static List<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
            return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(tagsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
