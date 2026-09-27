using Discovery.Api;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Support.Assignments;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Triagem por IA da auto-atribuição de chamados: consulta da decisão,
/// execução sob demanda (preview/aplicação) e auditoria.
///
/// A rodada automática acontece no background (Ao criar o chamado o
/// TicketCommandService apenas enfileira a triagem).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/tickets/assignment")]
public class TicketAssignmentController(IMediator mediator) : ControllerBase
{
    private Guid? CurrentUserId
        => HttpContext.Items["UserId"] is Guid uid ? uid : null;

    /// <summary>Última decisão da triagem por IA para o chamado (com os candidatos).</summary>
    [HttpGet("{ticketId:guid}/decision")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetDecision(Guid ticketId)
        => (await mediator.Send(new GetTicketAssignmentDecisionQuery(ticketId), HttpContext.RequestAborted))
            .ToActionResult();

    /// <summary>Executa a triagem por IA e devolve a sugestão, sem alterar o chamado.</summary>
    [HttpPost("{ticketId:guid}/preview")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Preview(Guid ticketId)
        => (await mediator.Send(
                new PreviewTicketAssignmentCommand(ticketId, CurrentUserId), HttpContext.RequestAborted))
            .ToActionResult();

    /// <summary>Executa a triagem por IA e aplica o responsável escolhido.</summary>
    [HttpPost("{ticketId:guid}/apply")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Apply(Guid ticketId)
        => (await mediator.Send(
                new ApplyTicketAssignmentCommand(ticketId, CurrentUserId), HttpContext.RequestAborted))
            .ToActionResult();

    /// <summary>Auditoria paginada das decisões da triagem por IA.</summary>
    [HttpGet("decisions")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> ListDecisions(
        [FromQuery] Guid? departmentId = null,
        [FromQuery] Guid? ticketId = null,
        [FromQuery] Guid? chosenUserId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
        => (await mediator.Send(new ListTicketAssignmentDecisionsQuery(
                departmentId, ticketId, chosenUserId, from, to, page, pageSize),
            HttpContext.RequestAborted)).ToActionResult();
}
