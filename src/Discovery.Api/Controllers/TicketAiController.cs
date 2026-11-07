using Discovery.Api.Filters;
using Discovery.Core.Cqrs.TicketAi.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Operações de IA sobre um chamado.
///
/// SEGURANÇA: todos os endpoints aqui operam SOBRE UM CHAMADO e antes só exigiam
/// <c>Tickets.View</c> global — nenhum deles validava o chamado. Consequências
/// reais: <c>/summarize</c> devolvia o conteúdo de um chamado de outro
/// departamento e <c>/draft-kb-article?persist=true</c> GRAVAVA um artigo na base
/// de conhecimento a partir dele.
///
/// A guarda correta é "pode TRATAR este chamado": resumir, triar e rascunhar
/// resposta são atividades de atendimento. O rascunho persistido exige, além
/// disso, permissão de escrita na base de conhecimento.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/tickets/{id:guid}/ai")]
public class TicketAiController(
    IMediator mediator,
    ITicketRepository ticketRepository,
    ITicketAccessPolicy accessPolicy,
    IPermissionService permissionService) : ControllerBase
{
    private Guid? CurrentUserId => HttpContext.Items["UserId"] as Guid?;

    /// <summary>
    /// Exige que o usuário possa TRATAR o chamado. Retorna null quando pode;
    /// caso contrário devolve o IActionResult de erro já montado.
    /// </summary>
    private async Task<IActionResult?> EnsureCanTreatAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound();

        var decision = await accessPolicy.CanTreatAsync(CurrentUserId, ticket, ct);
        decision = accessPolicy.ApplyShadow(decision, CurrentUserId, ticketId, "AiTreat");

        if (decision.Allowed)
            return null;

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = decision.Code,
            message = decision.Reason
        });
    }

    /// <summary>Sugestão de triagem: categoria, prioridade e departamento.</summary>
    [HttpPost("triage")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> Triage(Guid id, CancellationToken ct)
    {
        if (await EnsureCanTreatAsync(id, ct) is { } denied) return denied;

        var result = await mediator.Send(new TicketAiTriageCommand(id), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : StatusCode(503, new { error = errors[0].Message }));
    }

    /// <summary>Resumo executivo do ticket.</summary>
    [HttpPost("summarize")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> Summarize(Guid id, CancellationToken ct)
    {
        if (await EnsureCanTreatAsync(id, ct) is { } denied) return denied;

        var result = await mediator.Send(new TicketAiSummarizeCommand(id), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : StatusCode(503, new { error = errors[0].Message }));
    }

    /// <summary>Sugestão de resposta ao cliente.</summary>
    [HttpPost("suggest-reply")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> SuggestReply(Guid id, CancellationToken ct)
    {
        if (await EnsureCanTreatAsync(id, ct) is { } denied) return denied;

        var result = await mediator.Send(new TicketAiSuggestReplyCommand(id), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : StatusCode(503, new { error = errors[0].Message }));
    }

    /// <summary>
    /// Rascunho de artigo para base de conhecimento. Com <c>persist=true</c> o
    /// artigo é GRAVADO: além de tratar o chamado, exige escrita na KB.
    /// </summary>
    [HttpPost("draft-kb-article")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> DraftKbArticle(Guid id, [FromQuery] bool persist = false, CancellationToken ct = default)
    {
        if (await EnsureCanTreatAsync(id, ct) is { } denied) return denied;

        // Persistir CRIA conteúdo na base de conhecimento — não pode ser efeito
        // colateral de quem apenas visualiza chamados. A permissão checada é a do
        // recurso KnowledgeBase, não a do chamado.
        if (persist)
        {
            var userId = CurrentUserId;
            if (userId is null)
                return Unauthorized(new { message = "Autenticação necessária." });

            var canWriteKb = await permissionService.HasPermissionAsync(
                userId.Value, ResourceType.KnowledgeBase, ActionType.Edit);

            if (!canWriteKb)
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    code = "KB_WRITE_FORBIDDEN",
                    message = "Sem permissão para gravar artigo na base de conhecimento."
                });
        }

        var result = await mediator.Send(new TicketAiDraftKbArticleCommand(id, persist), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : StatusCode(503, new { error = errors[0].Message }));
    }
}
