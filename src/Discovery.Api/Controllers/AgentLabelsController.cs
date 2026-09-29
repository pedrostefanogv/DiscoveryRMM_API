using Discovery.Api.Filters;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Cqrs.AgentLabels.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

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
public class AgentLabelsController(IMediator mediator, ILabelReprocessQueue reprocessQueue) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetByAgent([FromQuery] Guid agentId)
    {
        var result = await mediator.Send(new ListAgentLabelsQuery(agentId));
        return result.ToActionResult();
    }

    [HttpGet("agents/{agentId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetByAgentId(Guid agentId)
    {
        var result = await mediator.Send(new ListAgentLabelsQuery(agentId));
        return result.ToActionResult();
    }

    /// <summary>
    /// DEPRECATED — use <c>POST /agent-labels/manual</c>. Alias mantido por
    /// compatibilidade (protegido por permissao); sera removido em uma janela futura.
    /// </summary>
    [Obsolete("Use POST /agent-labels/manual.")]
    [HttpPost]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> Add([FromBody] AddAgentLabelCommand cmd)
    {
        Response.Headers["Deprecation"] = "true";
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => Created("", dto), failure: errors => errors[0].Code == "Conflict" ? Conflict(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPost("manual")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> AddManual([FromBody] AddAgentLabelCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => Created("", dto), failure: errors => errors[0].Code == "Conflict" ? Conflict(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>
    /// DEPRECATED — use <c>DELETE /agent-labels/manual/{id}</c>. Alias mantido por
    /// compatibilidade; sera removido em uma janela futura.
    /// </summary>
    [Obsolete("Use DELETE /agent-labels/manual/{id}.")]
    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> Remove(Guid id)
    {
        Response.Headers["Deprecation"] = "true";
        var result = await mediator.Send(new RemoveAgentLabelCommand(id, Actor()));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("manual/{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> RemoveManual(Guid id)
    {
        var result = await mediator.Send(new RemoveAgentLabelCommand(id, Actor()));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Dispara o reprocessamento e devolve o jobId para acompanhamento.</summary>
    [HttpPost("reprocess")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> Reprocess()
    {
        var result = await mediator.Send(new ReprocessLabelsCommand(Actor()));
        return result.Match<IActionResult>(
            success: jobId => Ok(new { jobId, message = "Reprocessamento iniciado." }),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Consulta o progresso de um reprocessamento em andamento.</summary>
    [HttpGet("reprocess/{jobId}")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetReprocessStatus(string jobId, CancellationToken ct)
    {
        var status = await reprocessQueue.GetStatusAsync(jobId, ct);
        return status is null
            ? NotFound(new { errors = new[] { new { Code = "NotFound", Message = $"Job {jobId} nao encontrado." } } })
            : Ok(status);
    }

    /// <summary>Labels de vários agentes em uma única chamada.</summary>
    [HttpPost("batch")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetBatch([FromBody] ListAgentLabelsBatchRequest request)
    {
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
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetSuppressions(Guid agentId)
    {
        var result = await mediator.Send(new GetAgentLabelSuppressionsQuery(agentId));
        return result.ToActionResult();
    }

    /// <summary>Historico de mudancas de label do agente (Added/Removed), mais recente primeiro.</summary>
    [HttpGet("agents/{agentId:guid}/history")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetHistory(Guid agentId, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new GetAgentLabelHistoryQuery(agentId, limit));
        return result.ToActionResult();
    }

    /// <summary>Libera uma supressao: a label volta a ser aplicada na proxima reconciliacao.</summary>
    [HttpDelete("suppressions/{suppressionId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> ReleaseSuppression(Guid suppressionId)
    {
        var result = await mediator.Send(new ReleaseAgentLabelSuppressionCommand(suppressionId));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Labels com a contagem de agentes (filtro da lista de agentes).</summary>
    [HttpGet("usage")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetUsage([FromQuery] int limit = 200)
    {
        var result = await mediator.Send(new GetLabelUsageQuery(limit));
        return result.ToActionResult();
    }

    /// <summary>Ids de agentes que possuem uma label, paginados por cursor.</summary>
    [HttpGet("agents-by-label")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetAgentsByLabel(
        [FromQuery] string label,
        [FromQuery] Guid? afterAgentId = null,
        [FromQuery] int limit = 500)
    {
        var result = await mediator.Send(new GetAgentIdsByLabelQuery(label, afterAgentId, limit));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpGet("distinct")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetDistinct(
        [FromQuery] int limit = 500,
        [FromQuery] AgentLabelSourceType? sourceType = null)
    {
        var result = await mediator.Send(new GetDistinctLabelsQuery(limit, sourceType));
        return result.ToActionResult();
    }

    [HttpGet("rules")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetRules([FromQuery] bool includeDisabled = true)
    {
        var result = await mediator.Send(new ListLabelRulesQuery(includeDisabled));
        return result.ToActionResult();
    }

    [HttpGet("rules/{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetRuleById(Guid id)
    {
        var result = await mediator.Send(new GetLabelRuleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("rules")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
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
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
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
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> DeleteRule(Guid id)
    {
        var result = await mediator.Send(new DeleteLabelRuleCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Historico de versoes (configuracao) de uma regra, mais recente primeiro.</summary>
    [HttpGet("rules/{id:guid}/versions")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetRuleVersions(Guid id, [FromQuery] int limit = 20)
    {
        var result = await mediator.Send(new GetLabelRuleVersionsQuery(id, limit));
        return result.ToActionResult();
    }

    /// <summary>Lista as labels protegidas (nenhuma regra Remove pode apaga-las).</summary>
    [HttpGet("protected-labels")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetProtectedLabels()
    {
        var result = await mediator.Send(new GetProtectedLabelsQuery());
        return result.ToActionResult();
    }

    /// <summary>Adiciona uma label protegida (idempotente por nome).</summary>
    [HttpPost("protected-labels")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> AddProtectedLabel([FromBody] AddProtectedLabelCommand cmd)
    {
        var result = await mediator.Send(cmd with { CreatedBy = Actor() });
        return result.Match<IActionResult>(
            success: dto => Created("", dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Remove uma label da whitelist do modo Remover.</summary>
    [HttpDelete("protected-labels/{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
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
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> ExportRules()
    {
        var result = await mediator.Send(new ExportLabelRulesQuery());
        return result.ToActionResult();
    }

    /// <summary>Importa regras de um JSON exportado. Por padrao nao sobrescreve regras de mesmo nome.</summary>
    [HttpPost("rules/import")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> ImportRules([FromBody] AgentLabelRuleImportRequest request)
    {
        var result = await mediator.Send(new ImportLabelRulesCommand(request, Actor()));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Lists available Agent, Site and Client scoped custom fields usable in label rule expressions.</summary>
    [HttpGet("rules/available-custom-fields")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetAvailableCustomFields()
    {
        var result = await mediator.Send(new GetAvailableCustomFieldsQuery());
        return result.ToActionResult();
    }

    /// <summary>Lists agents currently matched by a label rule.</summary>
    [HttpGet("rules/{id:guid}/agents")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetAgentsByRule(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        var result = await mediator.Send(new ListAgentsByRuleQuery(id, page, pageSize));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Runs a dry-run (preview) of a rule expression against several agents in one call.</summary>
    [HttpPost("rules/dry-run/batch")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> DryRunBatch([FromBody] AgentLabelRuleDryRunBatchRequest request)
    {
        var result = await mediator.Send(new DryRunLabelRuleBatchQuery(request));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Estimates how many agents across the fleet a rule would affect.</summary>
    [HttpPost("rules/impact")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> EvaluateImpact([FromBody] AgentLabelRuleImpactRequest request)
    {
        var result = await mediator.Send(new EvaluateLabelRuleImpactQuery(request));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Runs a dry-run (preview) of a label rule expression against a single agent.</summary>
    [HttpPost("rules/dry-run")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> DryRun([FromBody] AgentLabelRuleDryRunRequest request)
    {
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
