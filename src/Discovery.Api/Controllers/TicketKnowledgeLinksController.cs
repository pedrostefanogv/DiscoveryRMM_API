using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// CRUD para links entre tickets e artigos da base de conhecimento.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/tickets/{ticketId:guid}/kb-links")]
public class TicketKnowledgeLinksController : ControllerBase
{
    private readonly ITicketKnowledgeLinkRepository _linkRepo;
    private readonly ITicketRepository _ticketRepo;
    private readonly IKnowledgeArticleRepository _knowledgeRepo;
    private readonly IScopeContext _scopeContext;
    private readonly IActivityLogService _activityLogService;

    public TicketKnowledgeLinksController(
        ITicketKnowledgeLinkRepository linkRepo,
        ITicketRepository ticketRepo,
        IKnowledgeArticleRepository knowledgeRepo,
        IScopeContext scopeContext,
        IActivityLogService activityLogService)
    {
        _linkRepo = linkRepo;
        _ticketRepo = ticketRepo;
        _knowledgeRepo = knowledgeRepo;
        _scopeContext = scopeContext;
        _activityLogService = activityLogService;
    }

    /// <summary>
    /// Lista artigos KB linkados a um ticket.
    /// </summary>
    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> List(Guid ticketId, CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(ticket.ClientId)
            && !(ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
            return Ok(Array.Empty<object>());

        var links = await _linkRepo.GetByTicketAsync(ticketId, ct);
        return Ok(links.Select(l => new
        {
            l.Id,
            l.TicketId,
            l.ArticleId,
            l.LinkedBy,
            l.Note,
            l.LinkedAt,
            l.FeedbackUseful,
            l.FeedbackAt
        }));
    }

    /// <summary>
    /// Linka um artigo KB a um ticket.
    /// </summary>
    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Link(Guid ticketId, [FromBody] LinkKbArticleRequest request, CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(ticket.ClientId)
            && !(ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
            return NotFound();

        // Verificar se artigo existe
        var article = await _knowledgeRepo.GetByIdAsync(request.ArticleId, ct);
        if (article is null) return BadRequest(new { error = "Artigo não encontrado." });

        // Evitar duplicata
        var existing = await _linkRepo.GetByTicketAndArticleAsync(ticketId, request.ArticleId, ct);
        if (existing is not null)
            return Ok(existing);

        var linkedBy = HttpContext.Items["Username"] as string ?? HttpContext.Items["UserId"]?.ToString();
        var link = await _linkRepo.CreateAsync(new TicketKnowledgeLink
        {
            TicketId = ticketId,
            ArticleId = request.ArticleId,
            LinkedBy = linkedBy,
            Note = request.Note
        }, ct);

        await _activityLogService.LogActivityAsync(
            ticketId, TicketActivityType.KnowledgeLinked,
            null, null, request.ArticleId.ToString(),
            $"Artigo KB '{article.Title}' linkado ao ticket");

        return CreatedAtAction(nameof(List), new { ticketId }, link);
    }

    /// <summary>
    /// Remove o link entre o ticket e um artigo KB.
    /// </summary>
    [HttpDelete("{linkId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Unlink(Guid ticketId, Guid linkId, CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(ticket.ClientId)
            && !(ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
            return NotFound();

        await _linkRepo.DeleteAsync(linkId, ct);

        await _activityLogService.LogActivityAsync(
            ticketId, TicketActivityType.KnowledgeUnlinked,
            null, linkId.ToString(), null,
            "Link de artigo KB removido");

        return NoContent();
    }

    /// <summary>
    /// Registra feedback em um link KB (útil/não útil).
    /// </summary>
    [HttpPost("{linkId:guid}/feedback")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Feedback(Guid ticketId, Guid linkId, [FromBody] KbLinkFeedbackRequest request, CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        await _linkRepo.SetFeedbackAsync(linkId, request.Useful, ct);
        return Ok(new { linkId, useful = request.Useful });
    }
}

public record LinkKbArticleRequest(Guid ArticleId, string? Note);
