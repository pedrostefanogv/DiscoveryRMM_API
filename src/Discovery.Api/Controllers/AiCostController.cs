using Discovery.Api.Filters;
using Discovery.Core.Cqrs.AiCost;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Painel de custo de IA (Feature). Agregação de tokens/custo por cliente a
/// partir do uso real persistido (ai_chat_messages), com orçamento mensal
/// opcional por cliente (AIIntegrationSettings.BudgetUsdMonthly).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/ai-cost")]
public class AiCostController(IMediator mediator) : ControllerBase
{
    [HttpGet("cockpit")]
    [RequirePermission(ResourceType.AiChat, ActionType.View)]
    public async Task<IActionResult> GetCockpit(
        [FromQuery] Guid? clientId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetAiCostCockpitQuery(clientId, from, to), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
