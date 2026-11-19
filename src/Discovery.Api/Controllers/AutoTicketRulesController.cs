using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.AutoTicketRules.Commands;
using Discovery.Core.Cqrs.AutoTicketRules.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Discovery.Api;
using Discovery.Api.Filters;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/auto-ticket-rules")]
public class AutoTicketRulesController(IMediator mediator) : ControllerBase
{
    private async Task<IActionResult?> EnsureScopeAccessAsync(
        string? scopeLevel,
        Guid? scopeId,
        ActionType action,
        IScopeContext scopeContext,
        ISiteRepository siteRepo,
        IAgentRepository agentRepo,
        bool allowGlobalRead)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, action);
        if (access.HasGlobalAccess)
            return null;

        var normalized = (scopeLevel ?? "Global").Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "global":
                return allowGlobalRead
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Apenas administradores globais podem alterar regras globais." });

            case "client":
                return scopeId.HasValue && access.AllowedClientIds.Contains(scopeId.Value)
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });

            case "site":
                if (!scopeId.HasValue)
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este site." });
                var site = await siteRepo.GetByIdAsync(scopeId.Value);
                if (site is null)
                    return NotFound(new { error = "Site not found." });
                return TenantScopePolicy.Allows(access, site.ClientId, scopeId.Value)
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este site." });

            case "agent":
                if (!scopeId.HasValue)
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este agente." });
                var agent = await agentRepo.GetByIdAsync(scopeId.Value);
                if (agent is null)
                    return NotFound(new { error = "Agent not found." });
                var agentSite = await siteRepo.GetByIdAsync(agent.SiteId);
                return TenantScopePolicy.Allows(access, agentSite?.ClientId ?? Guid.Empty, agent.SiteId)
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este agente." });

            default:
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Escopo não autorizado." });
        }
    }

    private async Task<IActionResult?> EnsureRuleAccessAsync(
        Guid ruleId,
        ActionType action,
        IScopeContext scopeContext,
        DiscoveryDbContext db,
        ISiteRepository siteRepo,
        IAgentRepository agentRepo,
        bool allowGlobalRead)
    {
        var rule = await db.AutoTicketRules.AsNoTracking()
            .Where(r => r.Id == ruleId)
            .Select(r => new { r.ScopeLevel, r.ScopeId })
            .FirstOrDefaultAsync();
        if (rule is null)
            return NotFound(new { error = "Auto-ticket rule not found." });

        return await EnsureScopeAccessAsync(rule.ScopeLevel.ToString(), rule.ScopeId, action, scopeContext, siteRepo, agentRepo, allowGlobalRead);
    }

    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] DiscoveryDbContext db,
        [FromQuery] string? scopeLevel = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] bool? isEnabled = null)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!access.HasGlobalAccess && (!string.IsNullOrWhiteSpace(scopeLevel) || scopeId.HasValue))
        {
            if (await EnsureScopeAccessAsync(scopeLevel, scopeId, ActionType.View, scopeContext, siteRepo, agentRepo, allowGlobalRead: true) is { } denied)
                return denied;
        }

        var result = await mediator.Send(new ListAutoTicketRulesQuery(scopeLevel, scopeId, isEnabled));
        if (result.IsFailure || access.HasGlobalAccess)
            return result.ToActionResult();

        var siteIds = result.Value!
            .Where(r => string.Equals(r.ScopeLevel, "Site", StringComparison.OrdinalIgnoreCase) && r.ScopeId.HasValue)
            .Select(r => r.ScopeId!.Value)
            .Distinct()
            .ToList();
        var agentIds = result.Value!
            .Where(r => string.Equals(r.ScopeLevel, "Agent", StringComparison.OrdinalIgnoreCase) && r.ScopeId.HasValue)
            .Select(r => r.ScopeId!.Value)
            .Distinct()
            .ToList();

        var agentSiteMap = agentIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.SiteId);
        foreach (var sId in agentSiteMap.Values)
            if (!siteIds.Contains(sId)) siteIds.Add(sId);

        var siteClientMap = siteIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await db.Sites.AsNoTracking().Where(s => siteIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.ClientId);

        var filtered = result.Value!.Where(r => r.ScopeLevel.ToLowerInvariant() switch
        {
            "global" => true,
            "client" => r.ScopeId.HasValue && access.AllowedClientIds.Contains(r.ScopeId.Value),
            "site" => r.ScopeId.HasValue && TenantScopePolicy.Allows(access, siteClientMap.GetValueOrDefault(r.ScopeId.Value), r.ScopeId.Value),
            "agent" => r.ScopeId.HasValue && agentSiteMap.TryGetValue(r.ScopeId.Value, out var aSite)
                && TenantScopePolicy.Allows(access, siteClientMap.GetValueOrDefault(aSite), aSite),
            _ => false
        }).ToList();

        return Ok(filtered);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo)
    {
        if (await EnsureRuleAccessAsync(id, ActionType.View, scopeContext, db, siteRepo, agentRepo, allowGlobalRead: true) is { } denied)
            return denied;

        var result = await mediator.Send(new GetAutoTicketRuleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Create(
        [FromBody] CreateAutoTicketRuleCommand cmd,
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo)
    {
        if (await EnsureScopeAccessAsync(cmd.ScopeLevel, cmd.ScopeId, ActionType.Edit, scopeContext, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
            return denied;

        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateAutoTicketRuleCommand cmd,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo)
    {
        if (await EnsureRuleAccessAsync(id, ActionType.Edit, scopeContext, db, siteRepo, agentRepo, allowGlobalRead: false) is { } deniedExisting)
            return deniedExisting;

        if (!string.IsNullOrWhiteSpace(cmd.ScopeLevel) || cmd.ScopeId.HasValue)
        {
            if (await EnsureScopeAccessAsync(cmd.ScopeLevel, cmd.ScopeId, ActionType.Edit, scopeContext, siteRepo, agentRepo, allowGlobalRead: false) is { } deniedTarget)
                return deniedTarget;
        }

        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo)
    {
        if (await EnsureRuleAccessAsync(id, ActionType.Edit, scopeContext, db, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
            return denied;

        var result = await mediator.Send(new DeleteAutoTicketRuleCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // O frontend chama PATCH /{id}/enable|disable. O handler de update já aplica
    // apenas os campos não nulos, então basta enviar IsEnabled.
    [HttpPatch("{id:guid}/enable")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public Task<IActionResult> Enable(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo) => SetEnabledAsync(id, true, scopeContext, db, siteRepo, agentRepo);

    [HttpPatch("{id:guid}/disable")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public Task<IActionResult> Disable(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo) => SetEnabledAsync(id, false, scopeContext, db, siteRepo, agentRepo);

    private async Task<IActionResult> SetEnabledAsync(
        Guid id,
        bool enabled,
        IScopeContext scopeContext,
        DiscoveryDbContext db,
        ISiteRepository siteRepo,
        IAgentRepository agentRepo)
    {
        if (await EnsureRuleAccessAsync(id, ActionType.Edit, scopeContext, db, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
            return denied;

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
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> Simulate(
        [FromBody] AutoTicketRuleDryRunRequest request,
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!access.HasGlobalAccess)
        {
            if (request.AgentId != Guid.Empty)
            {
                if (await EnsureScopeAccessAsync("Agent", request.AgentId, ActionType.View, scopeContext, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
                    return denied;
            }
            else if (request.SiteId.HasValue)
            {
                if (await EnsureScopeAccessAsync("Site", request.SiteId, ActionType.View, scopeContext, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
                    return denied;
            }
            else if (!access.AllowedClientIds.Contains(request.ClientId))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });
            }
        }

        var result = await mediator.Send(new AutoTicketRuleDryRunQuery(null, request));
        return result.ToActionResult();
    }

    /// <summary>Dry-run de uma regra específica.</summary>
    [HttpPost("{id:guid}/dry-run")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> DryRun(
        Guid id,
        [FromBody] AutoTicketRuleDryRunRequest request,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo)
    {
        if (await EnsureRuleAccessAsync(id, ActionType.View, scopeContext, db, siteRepo, agentRepo, allowGlobalRead: true) is { } denied)
            return denied;

        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!access.HasGlobalAccess)
        {
            if (request.AgentId != Guid.Empty)
            {
                if (await EnsureScopeAccessAsync("Agent", request.AgentId, ActionType.View, scopeContext, siteRepo, agentRepo, allowGlobalRead: false) is { } deniedAgent)
                    return deniedAgent;
            }
            else if (request.SiteId.HasValue)
            {
                if (await EnsureScopeAccessAsync("Site", request.SiteId, ActionType.View, scopeContext, siteRepo, agentRepo, allowGlobalRead: false) is { } deniedSite)
                    return deniedSite;
            }
            else if (!access.AllowedClientIds.Contains(request.ClientId))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });
            }
        }

        var result = await mediator.Send(new AutoTicketRuleDryRunQuery(id, request));
        return result.ToActionResult();
    }

    /// <summary>Estatísticas de decisão da regra na janela informada (default 168h).</summary>
    [HttpGet("{id:guid}/stats")]
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetStats(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        [FromQuery] int periodHours = 168)
    {
        if (await EnsureRuleAccessAsync(id, ActionType.View, scopeContext, db, siteRepo, agentRepo, allowGlobalRead: true) is { } denied)
            return denied;

        var result = await mediator.Send(new AutoTicketRuleStatsQuery(id, periodHours));
        return result.ToActionResult();
    }

    /// <summary>
    /// Semeia as regras padrão do cliente. Idempotente: rodar de novo atualiza
    /// em vez de duplicar.
    /// </summary>
    [HttpPost("seed-defaults")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> SeedDefaults(
        [FromBody] SeedDefaultAutoTicketRulesRequest request,
        [FromServices] IScopeContext scopeContext)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!access.HasGlobalAccess && !access.AllowedClientIds.Contains(request.ClientId))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });

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
    [RequirePermission(ResourceType.Tickets, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> Get(
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.View);
        if (!access.HasGlobalAccess)
        {
            if (siteId.HasValue)
            {
                var site = await siteRepo.GetByIdAsync(siteId.Value);
                if (site is null)
                    return NotFound(new { error = "Site not found." });
                if (!TenantScopePolicy.Allows(access, site.ClientId, siteId.Value))
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este site." });
            }
            else if (clientId.HasValue && !access.AllowedClientIds.Contains(clientId.Value))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });
            }
        }

        var result = await mediator.Send(new GetAutoTicketPolicyQuery(clientId, siteId));
        return result.ToActionResult();
    }

    /// <summary>Grava a política do nível global (Server).</summary>
    [HttpPut]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.Global)]
    public async Task<IActionResult> SaveServer([FromBody] AutoTicketPolicy policy)
    {
        var result = await mediator.Send(new SaveAutoTicketPolicyCommand("Server", null, policy));
        return result.ToActionResult();
    }

    /// <summary>Grava a política de um CLIENTE (sobrescreve apenas o que vier preenchido).</summary>
    [HttpPut("client/{clientId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.FromRoute)]
    public async Task<IActionResult> SaveClient(Guid clientId, [FromBody] AutoTicketPolicy policy)
    {
        var result = await mediator.Send(new SaveAutoTicketPolicyCommand("Client", clientId, policy));
        return result.ToActionResult();
    }

    /// <summary>Grava a política de um SITE (sobrescreve apenas o que vier preenchido).</summary>
    [HttpPut("site/{siteId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> SaveSite(
        Guid siteId,
        [FromBody] AutoTicketPolicy policy,
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
        if (!access.HasGlobalAccess)
        {
            var site = await siteRepo.GetByIdAsync(siteId);
            if (site is null)
                return NotFound(new { error = "Site not found." });
            if (!TenantScopePolicy.Allows(access, site.ClientId, siteId))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este site." });
        }

        var result = await mediator.Send(new SaveAutoTicketPolicyCommand("Site", siteId, policy));
        return result.ToActionResult();
    }
}
