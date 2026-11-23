using Discovery.Api.Filters;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationTasks.Commands;
using Discovery.Core.Cqrs.CustomFields.Queries;
using Discovery.Core.Cqrs.AutomationTasks.Queries;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/automation/tasks")]
public class AutomationTasksController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Automation, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? clientId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] AppApprovalScopeType? scopeType = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? search = null,
        [FromQuery] IReadOnlyList<AppApprovalScopeType>? scopeTypes = null,
        [FromQuery] IReadOnlyList<AutomationTaskActionType>? actionTypes = null,
        [FromQuery] IReadOnlyList<string>? labels = null,
        [FromQuery] bool activeOnly = false,
        [FromQuery] bool deletedOnly = false,
        [FromQuery] bool includeDeleted = false,
        [FromQuery] IReadOnlyList<string>? triggers = null,
        [FromQuery] bool? isActive = null)
    {
        var result = await mediator.Send(new ListAutomationTasksQuery(
            clientId, cursor, Math.Clamp(limit, 1, 200), siteId, agentId, scopeType, scopeId, search,
            scopeTypes, actionTypes, labels, activeOnly, deletedOnly, includeDeleted, triggers, isActive));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Automation, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetAutomationTaskByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: MapFailure);
    }

    [HttpGet("{id:guid}/audit")]
    [RequirePermission(ResourceType.Automation, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAudit(Guid id, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new GetAutomationTaskAuditQuery(id, limit));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}/executions")]
    [RequirePermission(ResourceType.Automation, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetExecutions(Guid id, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new GetAutomationTaskExecutionsQuery(id, Math.Clamp(limit, 1, 500)));
        return result.Match<IActionResult>(success: Ok, failure: MapFailure);
    }

    [HttpGet("{id:guid}/preview-targets")]
    [RequirePermission(ResourceType.Automation, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> PreviewTargets(Guid id, [FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        var result = await mediator.Send(new PreviewAutomationTaskTargetsQuery(id, Math.Clamp(limit, 1, 200), Math.Max(0, offset)));
        return result.Match<IActionResult>(success: Ok, failure: MapFailure);
    }

    [HttpPost]
    [RequirePermission(ResourceType.Automation, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> Create([FromBody] CreateAutomationTaskCommand cmd)
    {
        var result = await mediator.Send(cmd with
        {
            ChangedBy = CurrentActor(),
            IpAddress = ClientIp(),
            CorrelationId = CorrelationId()
        });
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: MapFailure);
    }

    /// <summary>
    /// Campos customizados vinculados a esta tarefa (visão reversa). Fica sob a
    /// autorização da automação, e não mais sob ServerConfig.
    /// </summary>
    [HttpGet("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Automation, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetCustomFields(Guid id)
    {
        var result = await mediator.Send(new ListCustomFieldsQuery(null, false, id, null));
        return result.ToActionResult();
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Automation, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAutomationTaskCommand cmd)
    {
        var result = await mediator.Send(cmd with
        {
            Id = id,
            ChangedBy = CurrentActor(),
            IpAddress = ClientIp(),
            CorrelationId = CorrelationId()
        });
        return result.Match<IActionResult>(success: Ok, failure: MapFailure);
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Automation, ActionType.Delete, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] string? reason = null)
    {
        var result = await mediator.Send(new DeleteAutomationTaskCommand(
            id,
            reason,
            CurrentActor(),
            ClientIp(),
            CorrelationId()));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: MapFailure);
    }

    [HttpPatch("{id:guid}/restore")]
    [RequirePermission(ResourceType.Automation, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Restore(Guid id, [FromQuery] string? reason = null)
    {
        var result = await mediator.Send(new RestoreAutomationTaskCommand(
            id,
            reason,
            CurrentActor(),
            ClientIp(),
            CorrelationId()));
        return result.Match<IActionResult>(success: Ok, failure: MapFailure);
    }

    private IActionResult MapFailure(IReadOnlyList<Error> errors)
    {
        var body = new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) };
        return errors[0].Code switch
        {
            "NotFound" => NotFound(body),
            "Forbidden" => StatusCode(StatusCodes.Status403Forbidden, body),
            _ => BadRequest(body)
        };
    }

    private string CurrentActor()
        => HttpContext?.Items["UserId"] is Guid userId
            ? $"user:{userId}"
            : HttpContext?.Items["Username"] as string ?? "system";

    private string? ClientIp() => HttpContext?.Connection.RemoteIpAddress?.ToString();

    private string CorrelationId()
        => HttpContext is not null && Request.Headers.TryGetValue("X-Correlation-Id", out var values) && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : "api";
}
