using Meduza.Core.DTOs.Mfa;
using Meduza.Core.DTOs.Users;
using Meduza.Core.Entities.Identity;
using Meduza.Core.Enums.Identity;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Core.Interfaces.Auth;
using Meduza.Core.Interfaces.Identity;
using Meduza.Core.Interfaces.Security;
using Meduza.Api.Filters;
using Meduza.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/users")]
[RequireUserAuth]
public class UsersController : ControllerBase
{
    private readonly IUserRepository _userRepo;
    private readonly IPasswordService _passwordService;
    private readonly IUserAuthService _userAuthService;
    private readonly IUserMfaKeyRepository _userMfaKeyRepository;
    private readonly MeshCentralIdentitySyncTriggerService _meshCentralSyncTrigger;

    public UsersController(
        IUserRepository userRepo,
        IPasswordService passwordService,
        IUserAuthService userAuthService,
        IUserMfaKeyRepository userMfaKeyRepository,
        MeshCentralIdentitySyncTriggerService meshCentralSyncTrigger)
    {
        _userRepo = userRepo;
        _passwordService = passwordService;
        _userAuthService = userAuthService;
        _userMfaKeyRepository = userMfaKeyRepository;
        _meshCentralSyncTrigger = meshCentralSyncTrigger;
    }

    [HttpGet]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetAll()
    {
        var users = await _userRepo.GetAllAsync();
        var result = users.Select(u => new UserSummaryDto
        {
            Id = u.Id,
            Login = u.Login,
            Email = u.Email,
            FullName = u.FullName,
            IsActive = u.IsActive,
            MfaConfigured = u.MfaConfigured
        });
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var user = await _userRepo.GetByIdAsync(id);
        if (user is null) return NotFound();

        return Ok(new UserDto
        {
            Id = user.Id,
            Login = user.Login,
            Email = user.Email,
            FullName = user.FullName,
            IsActive = user.IsActive,
            MfaRequired = user.MfaRequired,
            MfaConfigured = user.MfaConfigured,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt
        });
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMyProfile()
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var user = await _userRepo.GetByIdAsync(userId);
        if (user is null) return NotFound();

        return Ok(new UserDto
        {
            Id = user.Id,
            Login = user.Login,
            Email = user.Email,
            FullName = user.FullName,
            IsActive = user.IsActive,
            MfaRequired = user.MfaRequired,
            MfaConfigured = user.MfaConfigured,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt
        });
    }

    [HttpPut("me")]
    public async Task<IActionResult> UpdateMyProfile([FromBody] UpdateMyProfileDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var user = await _userRepo.GetByIdAsync(userId);
        if (user is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(dto.Email) &&
            !string.Equals(user.Email, dto.Email, StringComparison.OrdinalIgnoreCase) &&
            await _userRepo.ExistsByEmailAsync(dto.Email))
            return Conflict(new { message = "E-mail já em uso." });

        user.FullName = string.IsNullOrWhiteSpace(dto.FullName) ? user.FullName : dto.FullName;
        user.Email = string.IsNullOrWhiteSpace(dto.Email) ? user.Email : dto.Email;
        user.UpdatedAt = DateTime.UtcNow;

        await _userRepo.UpdateAsync(user);
        await _meshCentralSyncTrigger.OnUserUpdatedBestEffortAsync(user.Id, HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpGet("me/security")]
    public async Task<IActionResult> GetMySecurityProfile()
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var user = await _userRepo.GetByIdAsync(userId);
        if (user is null) return NotFound();

        var roleMfaRequirement = await _userAuthService.GetEffectiveMfaRequirementAsync(userId);
        var keys = await _userMfaKeyRepository.GetActiveByUserIdAsync(userId);

        return Ok(new MySecurityProfileDto
        {
            MfaRequired = user.MfaRequired,
            MfaConfigured = user.MfaConfigured,
            RoleMfaRequirement = roleMfaRequirement,
            Keys = keys.Select(k => new MyMfaKeySummaryDto
            {
                Id = k.Id,
                Name = k.Name,
                KeyType = k.KeyType,
                IsActive = k.IsActive,
                CreatedAt = k.CreatedAt,
                LastUsedAt = k.LastUsedAt
            }).ToList()
        });
    }

    [HttpPost("me/change-password")]
    public async Task<IActionResult> ChangeMyPassword([FromBody] ChangePasswordDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var user = await _userRepo.GetByIdAsync(userId);
        if (user is null) return NotFound();

        if (!_passwordService.VerifyPassword(dto.CurrentPassword, user.PasswordSalt, user.PasswordHash))
            return BadRequest(new { message = "Senha atual incorreta." });

        var (policyValid, policyReason) = _passwordService.ValidatePolicy(dto.NewPassword);
        if (!policyValid)
            return BadRequest(new { message = policyReason });

        var salt = _passwordService.GenerateSalt();
        var hash = _passwordService.HashPassword(dto.NewPassword, salt);

        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.UpdatedAt = DateTime.UtcNow;
        await _userRepo.UpdateAsync(user);
        return NoContent();
    }

    [HttpPost]
    [RequirePermission(ResourceType.Users, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateUserDto dto)
    {
        if (await _userRepo.ExistsByLoginAsync(dto.Login))
            return Conflict(new { message = "Login já em uso." });
        if (await _userRepo.ExistsByEmailAsync(dto.Email))
            return Conflict(new { message = "E-mail já em uso." });

        var (policyValid, policyReason) = _passwordService.ValidatePolicy(dto.Password);
        if (!policyValid)
            return BadRequest(new { message = policyReason });

        var salt = _passwordService.GenerateSalt();
        var hash = _passwordService.HashPassword(dto.Password, salt);

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = IdGenerator.NewId(),
            Login = dto.Login,
            Email = dto.Email,
            FullName = dto.FullName,
            PasswordHash = hash,
            PasswordSalt = salt,
            IsActive = true,
            MfaRequired = true,
            MfaConfigured = false,
            MustChangePassword = true,
            MustChangeProfile = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _userRepo.CreateAsync(user);

        var meshSync = await _meshCentralSyncTrigger.OnUserCreatedAsync(user.Id, HttpContext.RequestAborted);
        return CreatedAtAction(nameof(GetById), new { id = user.Id }, new
        {
            id = user.Id,
            meshCentralSync = new
            {
                synced = meshSync.Synced,
                meshUsername = meshSync.MeshUsername,
                siteBindingsApplied = meshSync.SiteBindingsApplied,
                error = meshSync.Error
            }
        });
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserDto dto)
    {
        var user = await _userRepo.GetByIdAsync(id);
        if (user is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(dto.Email) &&
            !string.Equals(user.Email, dto.Email, StringComparison.OrdinalIgnoreCase) &&
            await _userRepo.ExistsByEmailAsync(dto.Email))
            return Conflict(new { message = "E-mail já em uso." });

        user.FullName = string.IsNullOrWhiteSpace(dto.FullName) ? user.FullName : dto.FullName;
        user.Email = string.IsNullOrWhiteSpace(dto.Email) ? user.Email : dto.Email;
        user.IsActive = dto.IsActive ?? user.IsActive;
        user.MfaRequired = dto.MfaRequired ?? user.MfaRequired;
        user.UpdatedAt = DateTime.UtcNow;

        await _userRepo.UpdateAsync(user);
        await _meshCentralSyncTrigger.OnUserUpdatedBestEffortAsync(user.Id, HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("{id:guid}/change-password")]
    public async Task<IActionResult> ChangePassword(Guid id, [FromBody] ChangePasswordDto dto)
    {
        var requestingUserId = (Guid)HttpContext.Items["UserId"]!;
        // Somente o proprio usuario ou admin pode alterar senha
        if (requestingUserId != id)
        {
            var hasPermission = HttpContext.Items["HasAdminScope"] is true;
            if (!hasPermission)
                return Forbid();
        }

        var user = await _userRepo.GetByIdAsync(id);
        if (user is null) return NotFound();

        if (!_passwordService.VerifyPassword(dto.CurrentPassword, user.PasswordSalt, user.PasswordHash))
            return BadRequest(new { message = "Senha atual incorreta." });

        var (policyValid, policyReason) = _passwordService.ValidatePolicy(dto.NewPassword);
        if (!policyValid)
            return BadRequest(new { message = policyReason });

        var salt = _passwordService.GenerateSalt();
        var hash = _passwordService.HashPassword(dto.NewPassword, salt);

        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.UpdatedAt = DateTime.UtcNow;
        await _userRepo.UpdateAsync(user);
        return NoContent();
    }

    // ── Admin MFA management ──────────────────────────────────────────────────

    [HttpGet("{id:guid}/mfa/keys")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetUserMfaKeys(Guid id)
    {
        var user = await _userRepo.GetByIdAsync(id);
        if (user is null) return NotFound();

        var keys = await _userMfaKeyRepository.GetActiveByUserIdAsync(id);
        var result = keys.Select(k => new AdminUserMfaKeyDto
        {
            Id = k.Id,
            Name = k.Name,
            KeyType = k.KeyType,
            CreatedAt = k.CreatedAt,
            LastUsedAt = k.LastUsedAt
        });
        return Ok(result);
    }

    [HttpDelete("{id:guid}/mfa")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> ResetUserMfa(Guid id)
    {
        var user = await _userRepo.GetByIdAsync(id);
        if (user is null) return NotFound();

        await _userMfaKeyRepository.DeactivateAllByUserIdAsync(id);
        await _userRepo.SetMfaConfiguredAsync(id, false);
        return NoContent();
    }

    [HttpDelete("{id:guid}/mfa/keys/{keyId:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RemoveUserMfaKey(Guid id, Guid keyId)
    {
        var user = await _userRepo.GetByIdAsync(id);
        if (user is null) return NotFound();

        var key = await _userMfaKeyRepository.GetByIdAsync(keyId);
        if (key is null || key.UserId != id || !key.IsActive) return NotFound();

        await _userMfaKeyRepository.DeactivateAsync(keyId, id);

        var remaining = await _userMfaKeyRepository.CountActiveByUserIdAsync(id);
        if (remaining == 0)
            await _userRepo.SetMfaConfiguredAsync(id, false);

        return NoContent();
    }

    [HttpPost("{id:guid}/force-password-reset")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> ForcePasswordReset(Guid id)
    {
        var user = await _userRepo.GetByIdAsync(id);
        if (user is null) return NotFound();

        user.MustChangePassword = true;
        user.UpdatedAt = DateTime.UtcNow;
        await _userRepo.UpdateAsync(user);
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var requestingUserId = (Guid)HttpContext.Items["UserId"]!;
        if (requestingUserId == id)
            return BadRequest(new { message = "Não é possível excluir a própria conta." });

        var user = await _userRepo.GetByIdAsync(id);
        if (user is null) return NotFound();

        // Soft-delete: desativa o usuário
        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;
        await _userRepo.UpdateAsync(user);
        await _meshCentralSyncTrigger.OnUserDeprovisionBestEffortAsync(user.Id, deleteRemoteUser: false, HttpContext.RequestAborted);
        return NoContent();
    }
}
