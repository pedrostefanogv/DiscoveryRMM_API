using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Roles.Commands;
using Discovery.Core.Cqrs.Roles.Queries;
using Discovery.Core.DTOs.Roles;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/roles")]
public class RolesController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetAll()
    {
        var result = await mediator.Send(new ListRolesQuery());
        return result.ToActionResult();
    }

    /// <summary>
    /// Catálogo de permissões. A restrição guid de {id:guid} já evita conflito de rota
    /// com /roles/permissions; a ordem é apenas legibilidade.
    /// </summary>
    [HttpGet("permissions")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetPermissionsCatalog()
    {
        var result = await mediator.Send(new ListPermissionsCatalogQuery());
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetRoleByIdQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Users, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateRoleCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRoleCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteRoleCommand(id));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Permissões da role ───────────────────────────────────────────────────

    [HttpGet("{id:guid}/permissions")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetRolePermissions(Guid id)
    {
        var result = await mediator.Send(new ListRolePermissionsQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/permissions")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> AddRolePermission(Guid id, [FromBody] AssignPermissionToRoleDto dto)
    {
        var result = await mediator.Send(new AddRolePermissionCommand(id, dto.PermissionId));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}/permissions/{permissionId:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RemoveRolePermission(Guid id, Guid permissionId)
    {
        var result = await mediator.Send(new RemoveRolePermissionCommand(id, permissionId));
        return result.ToActionResult();
    }
}
