using Discovery.Core.Enums;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Gerencia vínculos entre tickets e tarefas de automação, com suporte a aprovação.
/// </summary>
[ApiController]
[Route("api/tickets/{ticketId:guid}/automation-links")]
public class TicketAutomationLinksController : ControllerBase
{
    private readonly ITicketRepository _ticketRepo;
    private readonly ITicketAutomationLinkRepository _linkRepo;
    private readonly IActivityLogService _activityLogService;

    public TicketAutomationLinksController(
        ITicketRepository ticketRepo,
        ITicketAutomationLinkRepository linkRepo,
        IActivityLogService activityLogService)
    {
        _ticketRepo = ticketRepo;
        _linkRepo = linkRepo;
        _activityLogService = activityLogService;
    }

    /// <summary>
    /// Lista todos os vínculos com automação para um ticket.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid ticketId, CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket não encontrado." });

        var links = await _linkRepo.GetByTicketAsync(ticketId, ct);
        return Ok(links.Select(l => new
        {
            l.Id,
            l.TicketId,
            l.AutomationTaskDefinitionId,
            l.Status,
            StatusLabel = l.Status.ToString(),
            l.RequestedBy,
            l.ReviewedBy,
            l.Note,
            l.RequestedAt,
            l.ReviewedAt
        }));
    }

    /// <summary>
    /// Cria um vínculo entre o ticket e uma tarefa de automação (solicita execução).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid ticketId,
        [FromBody] CreateAutomationLinkRequest request,
        CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket não encontrado." });

        if (request.AutomationTaskDefinitionId == Guid.Empty)
            return BadRequest(new { error = "AutomationTaskDefinitionId é obrigatório." });

        var link = await _linkRepo.CreateAsync(new TicketAutomationLink
        {
            TicketId = ticketId,
            AutomationTaskDefinitionId = request.AutomationTaskDefinitionId,
            Status = TicketAutomationLinkStatus.Pending,
            RequestedBy = request.RequestedBy,
            Note = request.Note
        }, ct);

        await _activityLogService.LogActivityAsync(
            ticketId,
            TicketActivityType.AutomationLinked,
            null, null,
            request.RequestedBy ?? "system",
            $"Automação {request.AutomationTaskDefinitionId} vinculada ao ticket. Status: Pendente de aprovação.");

        return CreatedAtAction(nameof(List), new { ticketId }, new { link.Id, link.Status });
    }

    /// <summary>
    /// Aprova a execução da automação vinculada ao ticket.
    /// </summary>
    [HttpPatch("{linkId:guid}/approve")]
    public async Task<IActionResult> Approve(
        Guid ticketId,
        Guid linkId,
        [FromBody] ReviewAutomationLinkRequest request,
        CancellationToken ct)
    {
        var link = await _linkRepo.GetByIdAsync(linkId, ct);
        if (link is null || link.TicketId != ticketId)
            return NotFound(new { error = "Vínculo não encontrado." });

        if (link.Status != TicketAutomationLinkStatus.Pending)
            return BadRequest(new { error = $"Vínculo já foi {link.Status.ToString().ToLower()}." });

        link.Status = TicketAutomationLinkStatus.Approved;
        link.ReviewedBy = request.ReviewedBy;
        link.Note = request.Note ?? link.Note;
        link.ReviewedAt = DateTime.UtcNow;
        await _linkRepo.UpdateAsync(link, ct);

        await _activityLogService.LogActivityAsync(
            ticketId,
            TicketActivityType.AutomationApproved,
            null, null,
            request.ReviewedBy ?? "system",
            $"Execução da automação {link.AutomationTaskDefinitionId} aprovada.");

        return Ok(new { link.Id, link.Status, link.ReviewedBy, link.ReviewedAt });
    }

    /// <summary>
    /// Rejeita a execução da automação vinculada ao ticket.
    /// </summary>
    [HttpPatch("{linkId:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid ticketId,
        Guid linkId,
        [FromBody] ReviewAutomationLinkRequest request,
        CancellationToken ct)
    {
        var link = await _linkRepo.GetByIdAsync(linkId, ct);
        if (link is null || link.TicketId != ticketId)
            return NotFound(new { error = "Vínculo não encontrado." });

        if (link.Status != TicketAutomationLinkStatus.Pending)
            return BadRequest(new { error = $"Vínculo já foi {link.Status.ToString().ToLower()}." });

        link.Status = TicketAutomationLinkStatus.Rejected;
        link.ReviewedBy = request.ReviewedBy;
        link.Note = request.Note ?? link.Note;
        link.ReviewedAt = DateTime.UtcNow;
        await _linkRepo.UpdateAsync(link, ct);

        await _activityLogService.LogActivityAsync(
            ticketId,
            TicketActivityType.AutomationRejected,
            null, null,
            request.ReviewedBy ?? "system",
            $"Execução da automação {link.AutomationTaskDefinitionId} rejeitada.");

        return Ok(new { link.Id, link.Status, link.ReviewedBy, link.ReviewedAt });
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────

public record CreateAutomationLinkRequest(
    Guid AutomationTaskDefinitionId,
    string? RequestedBy,
    string? Note);

public record ReviewAutomationLinkRequest(
    string? ReviewedBy,
    string? Note);
