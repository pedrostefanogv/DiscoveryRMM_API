using Discovery.Api.Filters;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Cqrs.AgentLabels.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Discovery.Api;

namespace Discovery.Api.Controllers;

/// <summary>
/// Labels de agentes (manuais e automaticas) e regras de auto-labeling.
/// Toda action exige permissao explicita: leitura usa Agents/View e escrita
/// Agents/Edit. Antes, 10 dos 15 endpoints nao tinham autorizacao alguma —
/// qualquer usuario autenticado podia alterar regras que rotulam a frota inteira.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/agent-labels")]
// A tela vive na Automação (/automation/agent-labels), mas a permissão que governa
// os dados continua sendo Agents: as labels rotulam AGENTES. O PermissionGate da rota
// e o item de menu usam a MESMA família (Agents.View), para que ninguém veja o link e
// receba 403.
//
// NÃO empilhe [RequirePermission(ResourceType.Automation, ...)] aqui: cada atributo
// vira um filtro independente e o ASP.NET exige TODOS (AND). Isso tiraria o acesso de
// papéis que têm apenas Agents.View (ex.: Supervisor de Cliente, Support, Técnico de
// Acesso Remoto), que hoje enxergam a tela.
[RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
public class AgentLabelsController(IMediator mediator, ILabelReprocessQueue reprocessQueue) : ControllerBase
{
    private static async Task<IActionResult?> EnsureAgentAccessAsync(
        Guid agentId,
        ActionType action,
        IScopeContext scopeContext,
        IAgentRepository agentRepo,
        ISiteRepository siteRepo)
    {
        var agent = await agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return new NotFoundObjectResult(new { error = "Agent not found." });

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, action);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return new ObjectResult(new { error = "Você não tem acesso a este agente." }) { StatusCode = StatusCodes.Status403Forbidden };

        return null;
    }

    private static async Task<IActionResult?> EnsureAllAgentsAccessAsync(
        IReadOnlyCollection<Guid>? agentIds,
        ActionType action,
        IScopeContext scopeContext,
        DiscoveryDbContext db)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, action);
        if (access.HasGlobalAccess || agentIds is null || agentIds.Count == 0)
            return null;

        var idList = agentIds.Distinct().ToList();
        var agents = await db.Agents.AsNoTracking()
            .Where(a => idList.Contains(a.Id))
            .Select(a => new { a.Id, a.SiteId })
            .ToListAsync();

        var siteIds = agents.Select(a => a.SiteId).Distinct().ToList();
        var siteClientMap = await db.Sites.AsNoTracking()
            .Where(s => siteIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.ClientId);

        foreach (var ag in agents)
        {
            if (!TenantScopePolicy.Allows(access, siteClientMap.GetValueOrDefault(ag.SiteId), ag.SiteId))
                return new ObjectResult(new { error = "Sem acesso a um ou mais agentes solicitados." }) { StatusCode = StatusCodes.Status403Forbidden };
        }

        return null;
    }

    private static IQueryable<Guid> PermittedAgentIdsQuery(DiscoveryDbContext db, UserScopeAccess access)
    {
        var allowedClients = access.AllowedClientIds;
        var allowedSites = access.AllowedSiteIds;
        return db.Agents.AsNoTracking()
            .Where(a => a.DeletedAt == null &&
                (allowedSites.Contains(a.SiteId) ||
                 db.Sites.Any(s => s.Id == a.SiteId && allowedClients.Contains(s.ClientId))))
            .Select(a => a.Id);
    }

    [HttpGet]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetByAgent(
        [FromQuery] Guid agentId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.View, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var result = await mediator.Send(new ListAgentLabelsQuery(agentId));
        return result.ToActionResult();
    }

    [HttpGet("agents/{agentId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetByAgentId(
        Guid agentId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.View, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var result = await mediator.Send(new ListAgentLabelsQuery(agentId));
        return result.ToActionResult();
    }

    /// <summary>
    /// DEPRECATED — use <c>POST /agent-labels/manual</c>. Alias mantido por
    /// compatibilidade (protegido por permissao); sera removido em uma janela futura.
    /// </summary>
    [Obsolete("Use POST /agent-labels/manual.")]
    [HttpPost]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Add(
        [FromBody] AddAgentLabelCommand cmd,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo)
    {
        if (await EnsureAgentAccessAsync(cmd.AgentId, ActionType.Edit, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        Response.Headers["Deprecation"] = "true";
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => Created("", dto), failure: errors => errors[0].Code == "Conflict" ? Conflict(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPost("manual")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> AddManual(
        [FromBody] AddAgentLabelCommand cmd,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo)
    {
        if (await EnsureAgentAccessAsync(cmd.AgentId, ActionType.Edit, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => Created("", dto), failure: errors => errors[0].Code == "Conflict" ? Conflict(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>
    /// DEPRECATED — use <c>DELETE /agent-labels/manual/{id}</c>. Alias mantido por
    /// compatibilidade; sera removido em uma janela futura.
    /// </summary>
    [Obsolete("Use DELETE /agent-labels/manual/{id}.")]
    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Remove(
        Guid id,
        [FromServices] DiscoveryDbContext db,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo)
    {
        var labelAgentId = await db.AgentLabels.AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => (Guid?)l.AgentId)
            .FirstOrDefaultAsync();
        if (!labelAgentId.HasValue)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = $"Label {id} not found." } } });
        if (await EnsureAgentAccessAsync(labelAgentId.Value, ActionType.Edit, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        Response.Headers["Deprecation"] = "true";
        var result = await mediator.Send(new RemoveAgentLabelCommand(id, Actor()));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("manual/{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> RemoveManual(
        Guid id,
        [FromServices] DiscoveryDbContext db,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo)
    {
        var labelAgentId = await db.AgentLabels.AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => (Guid?)l.AgentId)
            .FirstOrDefaultAsync();
        if (!labelAgentId.HasValue)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = $"Label {id} not found." } } });
        if (await EnsureAgentAccessAsync(labelAgentId.Value, ActionType.Edit, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var result = await mediator.Send(new RemoveAgentLabelCommand(id, Actor()));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Dispara o reprocessamento e devolve o jobId para acompanhamento.</summary>
    [HttpPost("reprocess")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute, ScopeSource.Global)]
    public async Task<IActionResult> Reprocess()
    {
        var result = await mediator.Send(new ReprocessLabelsCommand(Actor()));
        return result.Match<IActionResult>(
            success: jobId => Ok(new { jobId, message = "Reprocessamento iniciado." }),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Consulta o progresso de um reprocessamento em andamento.</summary>
    [HttpGet("reprocess/{jobId}")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> GetReprocessStatus(string jobId, CancellationToken ct)
    {
        var status = await reprocessQueue.GetStatusAsync(jobId, ct);
        return status is null
            ? NotFound(new { errors = new[] { new { Code = "NotFound", Message = $"Job {jobId} nao encontrado." } } })
            : Ok(status);
    }

    /// <summary>Labels de vários agentes em uma única chamada.</summary>
    [HttpPost("batch")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetBatch(
        [FromBody] ListAgentLabelsBatchRequest request,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        if (await EnsureAllAgentsAccessAsync(request.AgentIds, ActionType.View, scopeContext, db) is { } denied)
            return denied;

        var result = await mediator.Send(new ListAgentLabelsBatchQuery(request.AgentIds));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>
    /// Supressoes de labels de um agente: labels automaticas removidas manualmente que o
    /// reconcile esta respeitando. Ficam visiveis para o usuario poder libera-las.
    /// </summary>
    [HttpGet("agents/{agentId:guid}/suppressions")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetSuppressions(
        Guid agentId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.View, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var result = await mediator.Send(new GetAgentLabelSuppressionsQuery(agentId));
        return result.ToActionResult();
    }

    /// <summary>Historico de mudancas de label do agente (Added/Removed), mais recente primeiro.</summary>
    [HttpGet("agents/{agentId:guid}/history")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetHistory(
        Guid agentId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        [FromQuery] int limit = 50)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.View, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var result = await mediator.Send(new GetAgentLabelHistoryQuery(agentId, limit));
        return result.ToActionResult();
    }

    /// <summary>Libera uma supressao: a label volta a ser aplicada na proxima reconciliacao.</summary>
    [HttpDelete("suppressions/{suppressionId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> ReleaseSuppression(
        Guid suppressionId,
        [FromServices] DiscoveryDbContext db,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo)
    {
        var suppressionAgentId = await db.AgentLabelSuppressions.AsNoTracking()
            .Where(s => s.Id == suppressionId)
            .Select(s => (Guid?)s.AgentId)
            .FirstOrDefaultAsync();
        if (!suppressionAgentId.HasValue)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = $"Suppression {suppressionId} not found." } } });
        if (await EnsureAgentAccessAsync(suppressionAgentId.Value, ActionType.Edit, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var result = await mediator.Send(new ReleaseAgentLabelSuppressionCommand(suppressionId));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Labels com a contagem de agentes (filtro da lista de agentes).</summary>
    [HttpGet("usage")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetUsage(
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromQuery] int limit = 200)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);
        if (!access.HasGlobalAccess)
        {
            var clampedLimit = Math.Clamp(limit, 1, 1000);
            var permittedAgents = PermittedAgentIdsQuery(db, access);
            var usage = await db.AgentLabels.AsNoTracking()
                .Where(l => permittedAgents.Contains(l.AgentId))
                .GroupBy(l => l.Label)
                .Select(g => new AgentLabelUsageDto { Label = g.Key, AgentCount = g.Select(x => x.AgentId).Distinct().Count() })
                .OrderByDescending(u => u.AgentCount)
                .ThenBy(u => u.Label)
                .Take(clampedLimit)
                .ToListAsync();
            return Ok(usage);
        }

        var result = await mediator.Send(new GetLabelUsageQuery(limit));
        return result.ToActionResult();
    }

    /// <summary>Ids de agentes que possuem uma label, paginados por cursor.</summary>
    [HttpGet("agents-by-label")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAgentsByLabel(
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromQuery] string label,
        [FromQuery] Guid? afterAgentId = null,
        [FromQuery] int limit = 500)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);
        if (!access.HasGlobalAccess)
        {
            var normalized = (label ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return BadRequest(new { errors = new[] { new { Code = "Validation", Message = "Label is required.", Field = "label" } } });

            var clampedLimit = Math.Clamp(limit, 1, 1000);
            var permittedAgents = PermittedAgentIdsQuery(db, access);
            var baseQuery = db.AgentLabels.AsNoTracking()
                .Where(l => l.Label == normalized && permittedAgents.Contains(l.AgentId))
                .Select(l => l.AgentId)
                .Distinct();

            var total = await baseQuery.CountAsync();
            if (afterAgentId.HasValue)
                baseQuery = baseQuery.Where(id => id.CompareTo(afterAgentId.Value) > 0);

            var ids = await baseQuery.OrderBy(id => id).Take(clampedLimit + 1).ToListAsync();
            var hasMore = ids.Count > clampedLimit;
            var page = hasMore ? ids.Take(clampedLimit).ToList() : ids;
            return Ok(new AgentIdsByLabelResponse
            {
                Label = normalized,
                Total = total,
                AgentIds = page,
                NextCursor = hasMore && page.Count > 0 ? page[^1] : null,
                HasMore = hasMore,
                Limit = clampedLimit
            });
        }

        var result = await mediator.Send(new GetAgentIdsByLabelQuery(label, afterAgentId, limit));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpGet("distinct")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetDistinct(
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromQuery] int limit = 500,
        [FromQuery] AgentLabelSourceType? sourceType = null)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);
        if (!access.HasGlobalAccess)
        {
            var clampedLimit = Math.Clamp(limit, 1, 1000);
            var permittedAgents = PermittedAgentIdsQuery(db, access);
            var query = db.AgentLabels.AsNoTracking()
                .Where(l => permittedAgents.Contains(l.AgentId));
            if (sourceType.HasValue)
                query = query.Where(l => l.SourceType == sourceType.Value);

            var labels = await query
                .Select(l => l.Label)
                .Distinct()
                .OrderBy(l => l)
                .Take(clampedLimit)
                .ToListAsync();
            return Ok(labels);
        }

        var result = await mediator.Send(new GetDistinctLabelsQuery(limit, sourceType));
        return result.ToActionResult();
    }

    [HttpGet("rules")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> GetRules([FromQuery] bool includeDisabled = true)
    {
        var result = await mediator.Send(new ListLabelRulesQuery(includeDisabled));
        return result.ToActionResult();
    }

    [HttpGet("rules/{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> GetRuleById(Guid id)
    {
        var result = await mediator.Send(new GetLabelRuleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("rules")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.Global)]
    public async Task<IActionResult> CreateRule([FromBody] CreateAgentLabelRuleRequest request)
    {
        // O command guarda a expressao como JSON; o DTO da API a expoe como objeto.
        // Sem esta traducao o front enviava "expression" e o command recebia
        // ExpressionJson = null (400 "Expression is required").
        var cmd = new CreateLabelRuleCommand(
            request.Name,
            request.Label,
            request.Description,
            request.IsEnabled,
            request.ApplyMode.ToString(),
            AgentLabelExpressionJson.Serialize(request.Expression),
            Actor(),
            request.LabelMatch.ToString());

        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetRuleById), new { id = dto.Id }, dto),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("rules/{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.Global)]
    public async Task<IActionResult> UpdateRule(Guid id, [FromBody] UpdateAgentLabelRuleRequest request)
    {
        var cmd = new UpdateLabelRuleCommand(
            id,
            request.Name,
            request.Label,
            request.Description,
            request.IsEnabled,
            request.ApplyMode.ToString(),
            AgentLabelExpressionJson.Serialize(request.Expression),
            Actor(),
            request.LabelMatch.ToString());

        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("rules/{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.Global)]
    public async Task<IActionResult> DeleteRule(Guid id)
    {
        var result = await mediator.Send(new DeleteLabelRuleCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Historico de versoes (configuracao) de uma regra, mais recente primeiro.</summary>
    [HttpGet("rules/{id:guid}/versions")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> GetRuleVersions(Guid id, [FromQuery] int limit = 20)
    {
        var result = await mediator.Send(new GetLabelRuleVersionsQuery(id, limit));
        return result.ToActionResult();
    }

    /// <summary>Lista as labels protegidas (nenhuma regra Remove pode apaga-las).</summary>
    [HttpGet("protected-labels")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> GetProtectedLabels()
    {
        var result = await mediator.Send(new GetProtectedLabelsQuery());
        return result.ToActionResult();
    }

    /// <summary>Adiciona uma label protegida (idempotente por nome).</summary>
    [HttpPost("protected-labels")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.Global)]
    public async Task<IActionResult> AddProtectedLabel([FromBody] AddProtectedLabelCommand cmd)
    {
        var result = await mediator.Send(cmd with { CreatedBy = Actor() });
        return result.Match<IActionResult>(
            success: dto => Created("", dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Remove uma label da whitelist do modo Remover.</summary>
    [HttpDelete("protected-labels/{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.Global)]
    public async Task<IActionResult> RemoveProtectedLabel(Guid id)
    {
        var result = await mediator.Send(new RemoveProtectedLabelCommand(id));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Exporta todas as regras em JSON portavel (backup / promocao entre ambientes).</summary>
    [HttpGet("rules/export")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> ExportRules()
    {
        var result = await mediator.Send(new ExportLabelRulesQuery());
        return result.ToActionResult();
    }

    /// <summary>Importa regras de um JSON exportado. Por padrao nao sobrescreve regras de mesmo nome.</summary>
    [HttpPost("rules/import")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.Global)]
    public async Task<IActionResult> ImportRules([FromBody] AgentLabelRuleImportRequest request)
    {
        var result = await mediator.Send(new ImportLabelRulesCommand(request, Actor()));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Lists available Agent, Site and Client scoped custom fields usable in label rule expressions.</summary>
    [HttpGet("rules/available-custom-fields")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> GetAvailableCustomFields()
    {
        var result = await mediator.Send(new GetAvailableCustomFieldsQuery());
        return result.ToActionResult();
    }

    /// <summary>Lists agents currently matched by a label rule.</summary>
    [HttpGet("rules/{id:guid}/agents")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAgentsByRule(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var result = await mediator.Send(new ListAgentsByRuleQuery(id, page, pageSize));
        if (result.IsSuccess)
        {
            var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);
            if (!access.HasGlobalAccess)
            {
                var permittedIds = (await PermittedAgentIdsQuery(db, access).ToListAsync()).ToHashSet();
                var resp = result.Value!;
                var filtered = resp.Agents.Where(a => permittedIds.Contains(a.AgentId)).ToList();
                resp.Agents = filtered;
                resp.TotalAgents = filtered.Count;
                return Ok(resp);
            }
        }

        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Runs a dry-run (preview) of a rule expression against several agents in one call.</summary>
    [HttpPost("rules/dry-run/batch")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> DryRunBatch(
        [FromBody] AgentLabelRuleDryRunBatchRequest request,
        [FromServices] IScopeContext scopeContext,
        [FromServices] DiscoveryDbContext db)
    {
        if (await EnsureAllAgentsAccessAsync(request.AgentIds, ActionType.View, scopeContext, db) is { } denied)
            return denied;

        var result = await mediator.Send(new DryRunLabelRuleBatchQuery(request));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Estimates how many agents across the fleet a rule would affect.</summary>
    [HttpPost("rules/impact")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> EvaluateImpact([FromBody] AgentLabelRuleImpactRequest request)
    {
        var result = await mediator.Send(new EvaluateLabelRuleImpactQuery(request));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Runs a dry-run (preview) of a label rule expression against a single agent.</summary>
    [HttpPost("rules/dry-run")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> DryRun(
        [FromBody] AgentLabelRuleDryRunRequest request,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo)
    {
        if (await EnsureAgentAccessAsync(request.AgentId, ActionType.View, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var result = await mediator.Send(new DryRunLabelRuleQuery(request));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>
    /// Ator da acao para auditoria (mesma convencao do AgentUpdatesController):
    /// usuario autenticado vira "user:{id}"; token de API usa o username; jobs usam "system".
    /// </summary>
    private string Actor()
        => HttpContext.Items["UserId"] is Guid userId
            ? $"user:{userId}"
            : HttpContext.Items["Username"] as string ?? "system";
}
