using Discovery.Api.Filters;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints para merge e relações entre tickets.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/tickets/{id:guid}")]
public class TicketMergeRelationsController : ControllerBase
{
    private readonly ITicketMergeService _mergeService;
    private readonly ITicketRelationService _relationService;
    private readonly ITicketRepository _ticketRepo;
    private readonly IScopeContext _scopeContext;

    public TicketMergeRelationsController(
        ITicketMergeService mergeService,
        ITicketRelationService relationService,
        ITicketRepository ticketRepo,
        IScopeContext scopeContext)
    {
        _mergeService = mergeService;
        _relationService = relationService;
        _ticketRepo = ticketRepo;
        _scopeContext = scopeContext;
    }

    /// <summary>
    /// Faz merge de um ticket source no ticket target (id da rota).
    /// POST /tickets/{id}/merge
    /// </summary>
    [HttpPost("merge")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Merge(Guid id, [FromBody] MergeTicketRequest request)
    {
        var target = await _ticketRepo.GetByIdAsync(id);
        if (target is null) return NotFound("Target ticket not found.");

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(target.ClientId)
            && !(target.SiteId.HasValue && scope.AllowedSiteIds.Contains(target.SiteId.Value)))
            return NotFound();

        var mergedBy = HttpContext.Items["Username"] as string ?? HttpContext.Items["UserId"]?.ToString();
        var record = await _mergeService.MergeAsync(
            request.SourceTicketId, id, mergedBy, request.Reason);

        return Ok(new
        {
            message = "Merge completed successfully.",
            sourceTicketId = record.SourceTicketId,
            targetTicketId = record.TargetTicketId,
            mergedAt = record.MergedAt
        });
    }

    /// <summary>
    /// Cria uma relação entre tickets.
    /// POST /tickets/{id}/relations
    /// </summary>
    [HttpPost("relations")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> CreateRelation(Guid id, [FromBody] CreateTicketRelationRequest request)
    {
        var source = await _ticketRepo.GetByIdAsync(id);
        if (source is null) return NotFound("Source ticket not found.");

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(source.ClientId)
            && !(source.SiteId.HasValue && scope.AllowedSiteIds.Contains(source.SiteId.Value)))
            return NotFound();

        var createdBy = HttpContext.Items["Username"] as string ?? HttpContext.Items["UserId"]?.ToString();
        var relation = await _relationService.CreateRelationAsync(
            id, request.TargetTicketId, request.RelationType, createdBy);

        return CreatedAtAction(nameof(GetRelations), new { id }, relation);
    }

    /// <summary>
    /// Lista todas as relações de um ticket.
    /// GET /tickets/{id}/relations
    /// </summary>
    [HttpGet("relations")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetRelations(Guid id)
    {
        var ticket = await _ticketRepo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(ticket.ClientId)
            && !(ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
            return Ok(Array.Empty<object>());

        var relations = await _relationService.GetRelationsAsync(id);
        return Ok(relations.Select(r => new
        {
            r.Id,
            r.SourceTicketId,
            r.TargetTicketId,
            RelationType = ((TicketRelationType)r.RelationTypeValue).ToString(),
            r.CreatedBy,
            r.CreatedAt
        }));
    }

    /// <summary>
    /// Remove uma relação.
    /// DELETE /tickets/{id}/relations/{relationId}
    /// </summary>
    [HttpDelete("relations/{relationId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> RemoveRelation(Guid id, Guid relationId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!scope.HasGlobalAccess
            && !scope.AllowedClientIds.Contains(ticket.ClientId)
            && !(ticket.SiteId.HasValue && scope.AllowedSiteIds.Contains(ticket.SiteId.Value)))
            return NotFound();

        await _relationService.RemoveRelationAsync(relationId);
        return NoContent();
    }
}

public record MergeTicketRequest(Guid SourceTicketId, string? Reason);

public record CreateTicketRelationRequest(Guid TargetTicketId, TicketRelationType RelationType);
