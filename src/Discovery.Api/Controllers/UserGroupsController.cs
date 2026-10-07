using Discovery.Api.Filters;
using Discovery.Core.Cqrs.UserGroups.Commands;
using Discovery.Core.Cqrs.UserGroups.Queries;
using Discovery.Core.DTOs.Groups;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/user-groups")]
public class UserGroupsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetAll()
    {
        var result = await mediator.Send(new ListUserGroupsQuery());
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetUserGroupByIdQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Users, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateUserGroupCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserGroupCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteUserGroupCommand(id));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Membros ──────────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/members")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetMembers(Guid id)
    {
        var result = await mediator.Send(new ListGroupMembersQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/members")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddGroupMemberDto dto)
    {
        var result = await mediator.Send(new AddGroupMemberCommand(id, dto.UserId));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        var result = await mediator.Send(new RemoveGroupMemberCommand(id, userId));
        return result.ToActionResult();
    }

    // ── Roles do grupo ───────────────────────────────────────────────────────

    [HttpGet("{id:guid}/roles")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetRoles(Guid id)
    {
        var result = await mediator.Send(new ListGroupRolesQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/roles")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] AddGroupRoleDto dto)
    {
        var result = await mediator.Send(new AssignGroupRoleCommand(id, dto.RoleId, dto.ScopeLevel, dto.ScopeId));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}/roles/{assignmentId:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RemoveRole(Guid id, Guid assignmentId)
    {
        var result = await mediator.Send(new RemoveGroupRoleAssignmentCommand(id, assignmentId));
        return result.ToActionResult();
    }
}
