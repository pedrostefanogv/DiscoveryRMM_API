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
            publishedOnly: true,
            category: null,
            ct: ct);
        return Ok(articles);
    }

    [HttpGet("knowledge/{articleId:guid}")]
    public async Task<IActionResult> GetKnowledgeArticle(Guid articleId, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var article = await _knowledgeRepo.GetByIdAsync(articleId, ct);
        return article is null ? NotFound(new { error = "Article not found." }) : Ok(article);
    }
}
