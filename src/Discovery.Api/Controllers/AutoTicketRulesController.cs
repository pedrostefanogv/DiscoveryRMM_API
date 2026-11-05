using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.AutoTicketRules.Commands;
using Discovery.Core.Cqrs.AutoTicketRules.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;
using Discovery.Api.Filters;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/auto-ticket-rules")]
public class AutoTicketRulesController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] string? scopeLevel = null, [FromQuery] Guid? scopeId = null, [FromQuery] bool? isEnabled = null)
    {
        var result = await mediator.Send(new ListAutoTicketRulesQuery(scopeLevel, scopeId, isEnabled));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetAutoTicketRuleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Create([FromBody] CreateAutoTicketRuleCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAutoTicketRuleCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteAutoTicketRuleCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // O frontend chama PATCH /{id}/enable|disable. O handler de update já aplica
    // apenas os campos não nulos, então basta enviar IsEnabled.
    [HttpPatch("{id:guid}/enable")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public Task<IActionResult> Enable(Guid id) => SetEnabledAsync(id, true);

    [HttpPatch("{id:guid}/disable")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public Task<IActionResult> Disable(Guid id) => SetEnabledAsync(id, false);

    private async Task<IActionResult> SetEnabledAsync(Guid id, bool enabled)
    {
        var result = await mediator.Send(new UpdateAutoTicketRuleCommand(
            id, null, enabled, null, null, null, null, string.Empty, null, null, null, null, null));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Endpoints que o frontend JÁ chamava (src/api/auto-ticket-rules.ts)
    // mas que não existiam no backend — contrato morto até aqui.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Simulação SEM gravar nada: resolve a política, roda o motor de regras e o
    /// avaliador de sinal, e devolve o que aconteceria com o evento informado.
    /// </summary>
    [HttpPost("simulate")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> Simulate([FromBody] AutoTicketRuleDryRunRequest request)
    {
        var result = await mediator.Send(new AutoTicketRuleDryRunQuery(null, request));
        return result.ToActionResult();
    }

    /// <summary>Dry-run de uma regra específica.</summary>
    [HttpPost("{id:guid}/dry-run")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> DryRun(Guid id, [FromBody] AutoTicketRuleDryRunRequest request)
    {
        var result = await mediator.Send(new AutoTicketRuleDryRunQuery(id, request));
        return result.ToActionResult();
    }

    /// <summary>Estatísticas de decisão da regra na janela informada (default 168h).</summary>
    [HttpGet("{id:guid}/stats")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> GetStats(Guid id, [FromQuery] int periodHours = 168)
    {
        var result = await mediator.Send(new AutoTicketRuleStatsQuery(id, periodHours));
        return result.ToActionResult();
    }

    /// <summary>
    /// Semeia as regras padrão do cliente. Idempotente: rodar de novo atualiza
    /// em vez de duplicar.
    /// </summary>
    [HttpPost("seed-defaults")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> SeedDefaults([FromBody] SeedDefaultAutoTicketRulesRequest request)
    {
        var result = await mediator.Send(new SeedDefaultAutoTicketRulesCommand(request));
        return result.ToActionResult();
    }
}

/// <summary>
/// Política de auto-ticket com herança Server -> Client -> Site.
/// Rota separada porque a política não é uma regra: é a configuração que decide
/// SE o auto-ticket opera naquele escopo.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/auto-ticket-policy")]
public class AutoTicketPolicyController(IMediator mediator) : ControllerBase
{
    /// <summary>
    /// Devolve o blob do nível pedido, a política EFETIVA e a PROVENIÊNCIA de
    /// cada campo — permite responder "por que este site não abre chamado?".
    /// Sem clientId/siteId, resolve o nível global.
    /// </summary>
    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> Get([FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null)
    {
        var result = await mediator.Send(new GetAutoTicketPolicyQuery(clientId, siteId));
        return result.ToActionResult();
    }

    /// <summary>Grava a política do nível global (Server).</summary>
    [HttpPut]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> SaveServer([FromBody] AutoTicketPolicy policy)
    {
        var result = await mediator.Send(new SaveAutoTicketPolicyCommand("Server", null, policy));
        return result.ToActionResult();
    }

    /// <summary>Grava a política de um CLIENTE (sobrescreve apenas o que vier preenchido).</summary>
    [HttpPut("client/{clientId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> SaveClient(Guid clientId, [FromBody] AutoTicketPolicy policy)
    {
        var result = await mediator.Send(new SaveAutoTicketPolicyCommand("Client", clientId, policy));
        return result.ToActionResult();
    }

    /// <summary>Grava a política de um SITE (sobrescreve apenas o que vier preenchido).</summary>
    [HttpPut("site/{siteId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
    public async Task<IActionResult> SaveSite(Guid siteId, [FromBody] AutoTicketPolicy policy)
    {
        var result = await mediator.Send(new SaveAutoTicketPolicyCommand("Site", siteId, policy));
        return result.ToActionResult();
    }
}
