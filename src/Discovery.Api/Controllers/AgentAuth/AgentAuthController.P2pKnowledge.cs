using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent P2P bootstrap and knowledge base endpoints.
/// </summary>
public partial class AgentAuthController
{
    private static readonly TimeSpan P2pPeerOnlineWindow = TimeSpan.FromMinutes(10);

    [HttpPost("me/p2p/bootstrap")]
    public async Task<IActionResult> P2pBootstrap([FromBody] P2pBootstrapRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var site = await _siteRepo.GetByIdAsync(agent!.SiteId);
        if (site is null) return NotFound(new { error = "Site not found for agent." });

        var entry = new AgentP2pBootstrap
        {
            AgentId = agentId,
            ClientId = site.ClientId,
            PeerId = request.PeerId,
            AddrsJson = JsonSerializer.Serialize(request.Addrs ?? Array.Empty<string>()),
            Port = request.Port,
            LastHeartbeatAt = DateTime.UtcNow
        };

        await _p2pBootstrapRepo.UpsertAsync(entry);

        // Agenda publicação de snapshot de descoberta P2P para o site (com debounce)
        _p2pDiscoveryService.SchedulePublish(agent!.SiteId);

        var onlineCutoff = DateTime.UtcNow - P2pPeerOnlineWindow;
        var peers = await _p2pBootstrapRepo.GetRandomPeersAsync(site.ClientId, agentId, count: 3, onlineCutoff);
        var peerDtos = peers.Select(p => new P2pBootstrapPeerDto(
            p.PeerId,
            DeserializeAddrs(p.AddrsJson),
            p.Port)).ToList();

        return Ok(new P2pBootstrapResponse(peerDtos));
    }

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

    private static IReadOnlyList<string> DeserializeAddrs(string? addrsJson)
    {
        if (string.IsNullOrWhiteSpace(addrsJson)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(addrsJson) ?? new List<string>(); }
        catch { return Array.Empty<string>(); }
    }
}
