using Meduza.Core.DTOs.Groups;
using Meduza.Core.Entities.Identity;
using Meduza.Core.Enums.Identity;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces.Identity;
using Meduza.Api.Filters;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/user-groups")]
[RequireUserAuth]
public class UserGroupsController : ControllerBase
{
    private readonly IUserGroupRepository _groupRepo;
    private readonly IRoleRepository _roleRepo;
    private readonly IMeshCentralIdentitySyncService _meshCentralIdentitySyncService;

    public UserGroupsController(
        IUserGroupRepository groupRepo,
        IRoleRepository roleRepo,
        IMeshCentralIdentitySyncService meshCentralIdentitySyncService)
    {
        _groupRepo = groupRepo;
        _roleRepo = roleRepo;
        _meshCentralIdentitySyncService = meshCentralIdentitySyncService;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    [HttpGet]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetAll()
    {
        var groups = await _groupRepo.GetAllAsync();
        var result = groups.Select(g => new GroupDto
        {
            Id = g.Id,
            Name = g.Name,
            Description = g.Description,
            IsActive = g.IsActive,
            CreatedAt = g.CreatedAt
        });
        return Ok(result);
    }

    [HttpGet("{id:guid}")]

    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var group = await _groupRepo.GetByIdAsync(id);
        if (group is null) return NotFound();

        return Ok(new GroupDto
        {
            Id = group.Id,
            Name = group.Name,
            Description = group.Description,
            IsActive = group.IsActive,
            CreatedAt = group.CreatedAt
        });
    }

    [HttpPost]

    [RequirePermission(ResourceType.Users, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateGroupDto dto)
    {
        var now = DateTime.UtcNow;
        var group = new UserGroup
        {
            Id = IdGenerator.NewId(),
            Name = dto.Name,
            Description = dto.Description,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _groupRepo.CreateAsync(group);
        return CreatedAtAction(nameof(GetById), new { id = group.Id }, new { id = group.Id });
    }

    [HttpPut("{id:guid}")]

    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateGroupDto dto)
    {
        var group = await _groupRepo.GetByIdAsync(id);
        if (group is null) return NotFound();

        group.Name = string.IsNullOrWhiteSpace(dto.Name) ? group.Name : dto.Name;
        group.Description = dto.Description ?? group.Description;
        group.IsActive = dto.IsActive ?? group.IsActive;
        group.UpdatedAt = DateTime.UtcNow;
        await _groupRepo.UpdateAsync(group);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]

    [RequirePermission(ResourceType.Users, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var group = await _groupRepo.GetByIdAsync(id);
        if (group is null) return NotFound();
        await _groupRepo.DeleteAsync(id);
        return NoContent();
    }

    // ── Membros ───────────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/members")]

    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetMembers(Guid id)
    {
        var members = await _groupRepo.GetMemberIdsAsync(id);
        return Ok(members);
    }

    [HttpPost("{id:guid}/members")]

    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddGroupMemberDto dto)
    {
        await _groupRepo.AddMemberAsync(id, dto.UserId);
        await _meshCentralIdentitySyncService.SyncUserScopesAsync(dto.UserId, HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]

    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        await _groupRepo.RemoveMemberAsync(id, userId);
        await _meshCentralIdentitySyncService.SyncUserScopesAsync(userId, HttpContext.RequestAborted);
        return NoContent();
    }

    // ── Atribuição de Roles ───────────────────────────────────────────────────

    [HttpGet("{id:guid}/roles")]

    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetRoles(Guid id)
    {
        var roles = await _groupRepo.GetRolesForGroupAsync(id);
        return Ok(roles);
    }

    [HttpPost("{id:guid}/roles")]

    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] AssignRoleToGroupDto dto)
    {
        await _groupRepo.AssignRoleAsync(new UserGroupRole
        {
            Id = IdGenerator.NewId(),
            GroupId = id,
            RoleId = dto.RoleId,
            ScopeLevel = dto.ScopeLevel,
            ScopeId = dto.ScopeId,
            AssignedAt = DateTime.UtcNow
        });

        var memberIds = await _groupRepo.GetMemberIdsAsync(id);
        foreach (var memberId in memberIds)
        {
            await _meshCentralIdentitySyncService.SyncUserScopesAsync(memberId, HttpContext.RequestAborted);
        }

        return NoContent();
    }

    [HttpDelete("{id:guid}/roles/{assignmentId:guid}")]

    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RemoveRoleAssignment(Guid id, Guid assignmentId)
    {
        var roles = await _groupRepo.GetRolesForGroupAsync(id);
        if (!roles.Any(role => role.Id == assignmentId))
            return NotFound();

        await _groupRepo.RemoveRoleAssignmentAsync(assignmentId);

        var memberIds = await _groupRepo.GetMemberIdsAsync(id);
        foreach (var memberId in memberIds)
        {
            await _meshCentralIdentitySyncService.SyncUserScopesAsync(memberId, HttpContext.RequestAborted);
        }

        return NoContent();
    }
}
