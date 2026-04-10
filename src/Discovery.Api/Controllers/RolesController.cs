using Discovery.Core.DTOs.Roles;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/roles")]
[RequireUserAuth]
public class RolesController : ControllerBase
{
    private readonly IRoleRepository _roleRepo;
    private readonly IMeshCentralRightsProfileRepository _meshCentralRightsProfileRepository;

    public RolesController(
        IRoleRepository roleRepo,
        IMeshCentralRightsProfileRepository meshCentralRightsProfileRepository)
    {
        _roleRepo = roleRepo;
        _meshCentralRightsProfileRepository = meshCentralRightsProfileRepository;
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
            MeshRightsMask = r.MeshRightsMask,
            MeshRightsProfile = r.MeshRightsProfile,
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
            MeshRightsMask = role.MeshRightsMask,
            MeshRightsProfile = role.MeshRightsProfile,
            CreatedAt = role.CreatedAt
        });
    }

    [HttpPost]
    [RequirePermission(ResourceType.Users, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateRoleDto dto)
    {
        var normalizedProfile = string.IsNullOrWhiteSpace(dto.MeshRightsProfile)
            ? null
            : dto.MeshRightsProfile.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(normalizedProfile))
        {
            var profileExists = await _meshCentralRightsProfileRepository.GetByNameAsync(normalizedProfile) is not null;
            if (!profileExists)
                return BadRequest(new { message = $"Perfil MeshCentral '{normalizedProfile}' não existe." });
        }

        var now = DateTime.UtcNow;
        var role = new Role
        {
            Id = IdGenerator.NewId(),
            Name = dto.Name,
            Description = dto.Description,
            Type = RoleType.Custom,
            IsSystem = false,
            MfaRequirement = dto.MfaRequirement,
            MeshRightsMask = dto.MeshRightsMask,
            MeshRightsProfile = normalizedProfile,
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
        role.MeshRightsMask = dto.MeshRightsMask ?? role.MeshRightsMask;
        if (dto.MeshRightsProfile is not null)
        {
            var normalizedProfile = string.IsNullOrWhiteSpace(dto.MeshRightsProfile)
                ? null
                : dto.MeshRightsProfile.Trim().ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(normalizedProfile))
            {
                var profileExists = await _meshCentralRightsProfileRepository.GetByNameAsync(normalizedProfile) is not null;
                if (!profileExists)
                    return BadRequest(new { message = $"Perfil MeshCentral '{normalizedProfile}' não existe." });
            }

            role.MeshRightsProfile = normalizedProfile;
        }
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
