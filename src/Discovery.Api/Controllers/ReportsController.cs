using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Reports.Queries;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/reports")]
public class ReportsController(
    IMediator mediator,
    IReportService reportService,
    IReportScopeResolver scopeResolver) : ControllerBase
{
    /// <summary>
    /// Identidade do usuario autenticado para auditoria. Vem do token/contexto,
    /// nunca do corpo da requisicao (evita falsificacao de autoria).
    /// </summary>
    private string? CurrentUserId()
        => HttpContext.Items["UserId"] is Guid userId ? userId.ToString() : User.Identity?.Name;

    /// <summary>
    /// Resolve o escopo efetivo do usuario. Nenhum clientId vindo da requisicao e
    /// aceito fora do escopo permitido.
    /// </summary>
    private Task<ReportScopeResolution> ResolveScopeAsync(ActionType action, Guid? clientId, Guid? siteId)
        => scopeResolver.ResolveAsync(ResourceType.Reports, action, clientId, siteId);

    private ObjectResult AccessDenied(string? message)
        => StatusCode(StatusCodes.Status403Forbidden, new { message = message ?? "Acesso negado." });

    /// <summary>ClientId efetivo: global respeita a escolha; demais usam o escopo resolvido.</summary>
    private static Guid? EffectiveClientId(ReportScopeResolution scope, Guid? requestedClientId)
        => scope.IsGlobal ? requestedClientId : scope.ClientId;

    // --- Executions ---
    [HttpGet]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll([FromQuery] Guid? clientId = null)
    {
        var scope = await ResolveScopeAsync(ActionType.View, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(BuildListQuery(scope, clientId, null));
        return result.ToActionResult();
    }

    // A rota precisa ser "executions/{id}" para casar com o contrato consumido
    // pela UI (GET /api/v1/reports/executions/{id}) e com o Location devolvido
    // pelo AcceptedAtAction de RunNow.
    [HttpGet("executions/{executionId:guid}")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(Guid executionId, [FromQuery] Guid? clientId = null)
    {
        var scope = await ResolveScopeAsync(ActionType.View, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(new GetReportExecutionQuery(executionId, EffectiveClientId(scope, clientId)));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Baixa o arquivo gerado por uma execucao concluida.
    /// O conteudo e servido pela API (stream), o que funciona tambem com o
    /// provedor de storage local — cuja URL pre-assinada nao e navegavel.
    /// </summary>
    [HttpGet("executions/{executionId:guid}/download")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> Download(Guid executionId, [FromQuery] Guid? clientId = null, CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(ActionType.View, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var download = await reportService.GetDownloadAsync(executionId, EffectiveClientId(scope, clientId), cancellationToken);
        if (download is null)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = $"No completed report file found for execution {executionId}." } } });

        return File(download.Content, download.ContentType, download.FileName, enableRangeProcessing: true);
    }

    // --- Dataset Catalog ---
    [HttpGet("datasets")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetDatasetCatalog()
    {
        var result = await mediator.Send(new GetReportDatasetCatalogQuery());
        return result.ToActionResult();
    }


    // --- Layout schema / autocomplete / joins ---
    [HttpGet("layout-schema")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetLayoutSchema()
    {
        var result = await mediator.Send(new GetReportLayoutSchemaQuery());
        return result.ToActionResult();
    }

    [HttpGet("join-compatibility")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetJoinCompatibility()
    {
        var result = await mediator.Send(new GetReportJoinCompatibilityQuery());
        return result.ToActionResult();
    }

    [HttpGet("autocomplete")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAutocomplete(
        [FromQuery] string? term = null,
        [FromQuery] int? datasetType = null,
        [FromQuery] string? alias = null)
    {
        var result = await mediator.Send(
            new GetReportFieldAutocompleteQuery(term, datasetType ?? 0, alias));
        return result.ToActionResult();
    }

    // --- Template history / library ---
    [HttpGet("templates/{id:guid}/history")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetTemplateHistory(Guid id, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new GetReportTemplateHistoryQuery(id, limit));
        return result.ToActionResult();
    }

    [HttpGet("templates/library")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> ListLibrary([FromQuery] int? datasetType = null)
    {
        var result = await mediator.Send(new ListReportLibraryQuery(datasetType));
        return result.ToActionResult();
    }

    [HttpPost("templates/library/{id:guid}/install")]
    [RequirePermission(ResourceType.Reports, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> InstallLibraryTemplate(Guid id, [FromQuery] Guid? clientId = null)
    {
        var scope = await ResolveScopeAsync(ActionType.Create, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(new InstallReportLibraryTemplateCommand(
            id,
            EffectiveClientId(scope, clientId),
            CurrentUserId()));

        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetTemplateById), new { id = dto.Id }, dto),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    // --- Templates ---
    [HttpGet("templates")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> ListTemplates(
        [FromQuery] Guid? clientId = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] int? datasetType = null)
    {
        var scope = await ResolveScopeAsync(ActionType.View, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(
            new ListReportTemplatesQuery(EffectiveClientId(scope, clientId), isActive, datasetType));
        return result.ToActionResult();
    }

    [HttpGet("templates/{id:guid}")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetTemplateById(Guid id, [FromQuery] Guid? clientId = null)
    {
        var scope = await ResolveScopeAsync(ActionType.View, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(new GetReportTemplateByIdQuery(id, EffectiveClientId(scope, clientId)));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("templates")]
    [RequirePermission(ResourceType.Reports, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> CreateTemplate([FromBody] CreateReportTemplateCommand cmd)
    {
        var scope = await ResolveScopeAsync(ActionType.Create, cmd.ClientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(cmd with
        {
            ClientId = EffectiveClientId(scope, cmd.ClientId),
            CreatedBy = CurrentUserId()
        });

        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetTemplateById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("templates/{id:guid}")]
    [RequirePermission(ResourceType.Reports, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] UpdateReportTemplateCommand cmd)
    {
        var scope = await ResolveScopeAsync(ActionType.Edit, cmd.ClientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(cmd with
        {
            Id = id,
            ClientId = EffectiveClientId(scope, cmd.ClientId),
            UpdatedBy = CurrentUserId()
        });

        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("templates/{id:guid}")]
    [RequirePermission(ResourceType.Reports, ActionType.Delete, ScopeSource.AccessList)]
    public async Task<IActionResult> DeleteTemplate(Guid id, [FromQuery] Guid? clientId = null)
    {
        var scope = await ResolveScopeAsync(ActionType.Delete, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(new DeleteReportTemplateCommand(id, EffectiveClientId(scope, clientId)));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // --- Schedules ---
    [HttpGet("schedules")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> ListSchedules([FromQuery] Guid? clientId = null, [FromQuery] bool? isActive = null)
    {
        var scope = await ResolveScopeAsync(ActionType.View, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(new ListReportSchedulesQuery(EffectiveClientId(scope, clientId), isActive));
        return result.ToActionResult();
    }

    [HttpGet("schedules/{id:guid}")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetScheduleById(Guid id, [FromQuery] Guid? clientId = null)
    {
        var scope = await ResolveScopeAsync(ActionType.View, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(new GetReportScheduleQuery(id, EffectiveClientId(scope, clientId)));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("schedules")]
    [RequirePermission(ResourceType.Reports, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> CreateSchedule([FromBody] CreateReportScheduleCommand cmd)
    {
        var scope = await ResolveScopeAsync(ActionType.Create, cmd.ClientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(cmd with
        {
            ClientId = EffectiveClientId(scope, cmd.ClientId),
            CreatedBy = CurrentUserId()
        });

        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetScheduleById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("schedules/{id:guid}")]
    [RequirePermission(ResourceType.Reports, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> UpdateSchedule(Guid id, [FromBody] UpdateReportScheduleCommand cmd)
    {
        var scope = await ResolveScopeAsync(ActionType.Edit, cmd.ClientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(cmd with
        {
            Id = id,
            ClientId = EffectiveClientId(scope, cmd.ClientId),
            UpdatedBy = CurrentUserId()
        });

        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("schedules/{id:guid}")]
    [RequirePermission(ResourceType.Reports, ActionType.Delete, ScopeSource.AccessList)]
    public async Task<IActionResult> DeleteSchedule(Guid id, [FromQuery] Guid? clientId = null)
    {
        var scope = await ResolveScopeAsync(ActionType.Delete, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(new DeleteReportScheduleCommand(id, EffectiveClientId(scope, clientId)));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // --- Run ---
    [HttpGet("executions")]
    [RequirePermission(ResourceType.Reports, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> ListExecutions([FromQuery] Guid? clientId = null, [FromQuery] int? limit = null)
    {
        var scope = await ResolveScopeAsync(ActionType.View, clientId, null);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(BuildListQuery(scope, clientId, limit));
        return result.ToActionResult();
    }

    private static ListReportsQuery BuildListQuery(ReportScopeResolution scope, Guid? requestedClientId, int? limit)
        => scope.IsGlobal
            ? new ListReportsQuery(requestedClientId, limit ?? 50)
            : new ListReportsQuery(null, limit ?? 50, scope.AllowedClientIds);

    [HttpPost("run")]
    [RequirePermission(ResourceType.Reports, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> RunNow([FromBody] RunReportNowCommand cmd)
    {
        var scope = await ResolveScopeAsync(ActionType.Execute, cmd.ClientId, cmd.SiteId);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(cmd with
        {
            ClientId = EffectiveClientId(scope, cmd.ClientId),
            SiteId = scope.IsGlobal ? cmd.SiteId : scope.SiteId,
            CreatedBy = CurrentUserId()
        });

        return result.Match<IActionResult>(
            success: dto => AcceptedAtAction(nameof(GetById), new { executionId = dto.ExecutionId }, dto),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    // --- Preview ---
    [HttpPost("preview")]
    [RequirePermission(ResourceType.Reports, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> Preview([FromBody] PreviewReportCommand cmd)
    {
        var scope = await ResolveScopeAsync(ActionType.Execute, cmd.ClientId, cmd.SiteId);
        if (!scope.Allowed)
            return AccessDenied(scope.Error);

        var result = await mediator.Send(cmd with
        {
            ClientId = EffectiveClientId(scope, cmd.ClientId),
            SiteId = scope.IsGlobal ? cmd.SiteId : scope.SiteId
        });

        return result.Match<IActionResult>(
            success: dto =>
            {
                Response.Headers["X-Report-RowCount"] = dto.RowCount?.ToString();
                Response.Headers["X-Report-Title"] = dto.Title;
                Response.Headers["X-Report-Format"] = dto.Format;
                Response.Headers["X-Report-Preview"] = "true";
                if (!string.IsNullOrWhiteSpace(dto.Disposition))
                    Response.Headers["Content-Disposition"] = dto.Disposition;

                if (!string.IsNullOrWhiteSpace(dto.Html))
                    return Content(dto.Html, dto.ContentType);

                if (dto.Content is not null)
                    return File(dto.Content, dto.ContentType);

                return NoContent();
            },
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }
}
