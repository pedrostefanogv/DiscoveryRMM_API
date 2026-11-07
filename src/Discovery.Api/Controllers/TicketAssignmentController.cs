using Discovery.Api;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Support.Assignments;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Triagem por IA da auto-atribuição de chamados: consulta da decisão,
/// execução sob demanda (preview/aplicação) e auditoria.
///
/// A rodada automática acontece no background (ao criar o chamado o
/// TicketCommandService apenas enfileira a triagem).
///
/// SEGURANÇA: estes endpoints operam sobre um CHAMADO e antes não validavam nada
/// além de <c>Tickets.View</c>/<c>Tickets.Edit</c> globais. Em particular
/// <c>/apply</c> ATRIBUÍA o chamado — um usuário de outro departamento podia
/// assumir um chamado que não era do seu time. Agora exigem "pode tratar".
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/tickets/assignment")]
public class TicketAssignmentController(
    IMediator mediator,
    ITicketRepository ticketRepository,
    ITicketAccessPolicy accessPolicy) : ControllerBase
{
    private Guid? CurrentUserId
        => HttpContext.Items["UserId"] is Guid uid ? uid : null;

    /// <summary>
    /// Exige que o usuário possa TRATAR o chamado (membro do time do
    /// departamento, ou acesso global). Devolve null quando permitido.
    /// </summary>
    private async Task<IActionResult?> EnsureCanTreatAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound();

        var decision = await accessPolicy.CanTreatAsync(CurrentUserId, ticket, ct);
        decision = accessPolicy.ApplyShadow(decision, CurrentUserId, ticketId, "Assignment");

        if (decision.Allowed)
            return null;

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = decision.Code,
            message = decision.Reason
        });
    }

    /// <summary>Última decisão da triagem por IA para o chamado (com os candidatos).</summary>
    [HttpGet("{ticketId:guid}/decision")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetDecision(Guid ticketId)
    {
        // A decisão expõe os CANDIDATOS (nomes e competências do time) — é dado
        // de atendimento, então exige a mesma guarda dos demais endpoints.
        if (await EnsureCanTreatAsync(ticketId, HttpContext.RequestAborted) is { } denied)
            return denied;

        return (await mediator.Send(new GetTicketAssignmentDecisionQuery(ticketId), HttpContext.RequestAborted))
            .ToActionResult();
    }

    /// <summary>Executa a triagem por IA e devolve a sugestão, sem alterar o chamado.</summary>
    [HttpPost("{ticketId:guid}/preview")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Preview(Guid ticketId)
    {
        if (await EnsureCanTreatAsync(ticketId, HttpContext.RequestAborted) is { } denied)
            return denied;

        return (await mediator.Send(
                new PreviewTicketAssignmentCommand(ticketId, CurrentUserId), HttpContext.RequestAborted))
            .ToActionResult();
    }

    /// <summary>Executa a triagem por IA e aplica o responsável escolhido.</summary>
    [HttpPost("{ticketId:guid}/apply")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Apply(Guid ticketId)
    {
        // ATRIBUI o chamado: sem esta guarda um usuário de fora assumia o chamado.
        if (await EnsureCanTreatAsync(ticketId, HttpContext.RequestAborted) is { } denied)
            return denied;

        return (await mediator.Send(
                new ApplyTicketAssignmentCommand(ticketId, CurrentUserId), HttpContext.RequestAborted))
            .ToActionResult();
    }

    /// <summary>
    /// Auditoria paginada das decisões da triagem por IA.
    /// Exige acesso global: é uma visão consolidada de TODOS os departamentos e
    /// não há um chamado único para aplicar a regra por registro.
    /// </summary>
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
    {
        if (!await accessPolicy.HasGlobalTicketAccessAsync(CurrentUserId, HttpContext.RequestAborted))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "AUDIT_REQUIRES_GLOBAL_ACCESS",
                message = "A auditoria de atribuições exige acesso global a chamados."
            });

        return (await mediator.Send(new ListTicketAssignmentDecisionsQuery(
                departmentId, ticketId, chosenUserId, from, to, page, pageSize),
            HttpContext.RequestAborted)).ToActionResult();
    }
}
