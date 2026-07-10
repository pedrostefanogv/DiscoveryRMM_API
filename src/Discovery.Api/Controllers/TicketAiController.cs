using Discovery.Api.Filters;
using Discovery.Core.Cqrs.TicketAi.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/tickets/{id:guid}/ai")]
public class TicketAiController(IMediator mediator) : ControllerBase
{
    /// <summary>Sugestão de triagem: categoria, prioridade e departamento.</summary>
    [HttpPost("triage")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> Triage(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new TicketAiTriageCommand(id), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : StatusCode(503, new { error = errors[0].Message }));
    }

    /// <summary>Resumo executivo do ticket.</summary>
    [HttpPost("summarize")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> Summarize(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new TicketAiSummarizeCommand(id), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : StatusCode(503, new { error = errors[0].Message }));
    }

    /// <summary>Sugestão de resposta ao cliente.</summary>
    [HttpPost("suggest-reply")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> SuggestReply(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new TicketAiSuggestReplyCommand(id), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : StatusCode(503, new { error = errors[0].Message }));
    }

    /// <summary>Rascunho de artigo para base de conhecimento.</summary>
    [HttpPost("draft-kb-article")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> DraftKbArticle(Guid id, [FromQuery] bool persist = false, CancellationToken ct = default)
    {
        var result = await mediator.Send(new TicketAiDraftKbArticleCommand(id, persist), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound() : StatusCode(503, new { error = errors[0].Message }));
    }
}
