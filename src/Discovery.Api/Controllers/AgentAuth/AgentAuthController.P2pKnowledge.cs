using Discovery.Core.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent P2P bootstrap and knowledge base endpoints.
/// </summary>
public partial class AgentAuthController
{
    [HttpPost("me/p2p/bootstrap")]
    public async Task<IActionResult> P2pBootstrap([FromBody] P2pBootstrapRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        // Read existing or create new
        var existing = await _p2pBootstrapRepo.GetByAgentIdAsync(agentId);
        if (existing is not null)
        {
            existing.PublicKey = request.PublicKey;
            existing.UpdatedAt = DateTime.UtcNow;
            await _p2pBootstrapRepo.UpdateAsync(existing);
            return Ok(new { message = "Bootstrap updated.", id = existing.Id });
        }

        var entry = new AgentP2pBootstrap
        {
            AgentId = agentId,
            PublicKey = request.PublicKey,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _p2pBootstrapRepo.CreateAsync(entry);
        return Ok(new { message = "Bootstrap created.", id = entry.Id });
    }

    [HttpGet("knowledge")]
    public async Task<IActionResult> GetKnowledgeArticles()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var articles = await _knowledgeRepo.GetPublishedAsync();
        return Ok(articles);
    }

    [HttpGet("knowledge/{articleId:guid}")]
    public async Task<IActionResult> GetKnowledgeArticle(Guid articleId)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var article = await _knowledgeRepo.GetByIdAsync(articleId);
        return article is null ? NotFound(new { error = "Article not found." }) : Ok(article);
    }
}
