using Meduza.Core.DTOs.Roles;
using Meduza.Core.Entities.Identity;
using Meduza.Core.Enums.Identity;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces.Identity;
using Meduza.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/roles")]
[RequireUserAuth]
public class RolesController : ControllerBase
{
    private readonly IRoleRepository _roleRepo;

    public RolesController(IRoleRepository roleRepo)
    {
        _roleRepo = roleRepo;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    [HttpGet]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetAll()
    {
        var roles = await _roleRepo.GetAllAsync();
        return Ok(roles.Select(r => new RoleDto
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            Type = r.Type,
            IsSystem = r.IsSystem,
            MfaRequirement = r.MfaRequirement,
            CreatedAt = r.CreatedAt
        }));
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var role = await _roleRepo.GetByIdAsync(id);
        if (role is null) return NotFound();
        return Ok(new RoleDto
        {
            Id = role.Id,
            Name = role.Name,
            Description = role.Description,
            Type = role.Type,
            IsSystem = role.IsSystem,
            MfaRequirement = role.MfaRequirement,
            CreatedAt = role.CreatedAt
        });
    }

    [HttpPost]
    [RequirePermission(ResourceType.Users, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateRoleDto dto)
    {
        var now = DateTime.UtcNow;
        var role = new Role
        {
            Id = IdGenerator.NewId(),
            Name = dto.Name,
            Description = dto.Description,
            Type = RoleType.Custom,
            IsSystem = false,
            MfaRequirement = dto.MfaRequirement,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _roleRepo.CreateAsync(role);
        return CreatedAtAction(nameof(GetById), new { id = role.Id }, new { id = role.Id });
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRoleDto dto)
    {
        var role = await _roleRepo.GetByIdAsync(id);
        if (role is null) return NotFound();

        if (role.IsSystem && (!string.IsNullOrWhiteSpace(dto.Name) || dto.Description is not null))
            return BadRequest(new { message = "Roles de sistema não podem ter nome/descrição alterados." });

        role.Name = string.IsNullOrWhiteSpace(dto.Name) ? role.Name : dto.Name;
        role.Description = dto.Description ?? role.Description;
        role.MfaRequirement = dto.MfaRequirement ?? role.MfaRequirement;
        role.UpdatedAt = DateTime.UtcNow;
        await _roleRepo.UpdateAsync(role);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var role = await _roleRepo.GetByIdAsync(id);
        if (role is null) return NotFound();
        if (role.IsSystem)
            return BadRequest(new { message = "Roles de sistema não podem ser excluídas." });
        await _roleRepo.DeleteAsync(id);
        return NoContent();
    }

    // ── Permissões ───────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/permissions")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetPermissions(Guid id)
    {
        var permissions = await _roleRepo.GetPermissionsForRoleAsync(id);
        return Ok(permissions.Select(p => new PermissionDto
        {
            Id = p.Id,
            ResourceType = p.ResourceType,
            ActionType = p.ActionType,
            Description = p.Description
        }));
    }

    [HttpGet("permissions")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetAllPermissions()
    {
        var permissions = await _roleRepo.GetAllPermissionsAsync();
        return Ok(permissions.Select(p => new PermissionDto
        {
            Id = p.Id,
            ResourceType = p.ResourceType,
            ActionType = p.ActionType,
            Description = p.Description
        }));
    }

    [HttpPost("{id:guid}/permissions")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> AddPermission(Guid id, [FromBody] AssignPermissionToRoleDto dto)
    {
        var role = await _roleRepo.GetByIdAsync(id);
        if (role is null) return NotFound();
        if (role.IsSystem)
            return BadRequest(new { message = "Não é possível modificar permissões de roles de sistema." });

        await _roleRepo.AddPermissionToRoleAsync(id, dto.PermissionId);
        return NoContent();
    }

    [HttpDelete("{id:guid}/permissions/{permissionId:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RemovePermission(Guid id, Guid permissionId)
    {
        var role = await _roleRepo.GetByIdAsync(id);
        if (role is null) return NotFound();
        if (role.IsSystem)
            return BadRequest(new { message = "Não é possível modificar permissões de roles de sistema." });

        await _roleRepo.RemovePermissionFromRoleAsync(id, permissionId);
        return NoContent();
    }
}
