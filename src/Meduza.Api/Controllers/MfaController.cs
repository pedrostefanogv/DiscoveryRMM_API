using Meduza.Core.DTOs.Mfa;
using Meduza.Core.Entities.Security;
using Meduza.Core.Enums.Security;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces.Auth;
using Meduza.Core.Interfaces.Identity;
using Meduza.Core.Interfaces.Security;
using Meduza.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/mfa")]
[RequireUserAuth]
public class MfaController : ControllerBase
{
    private readonly IFido2Service _fido2Service;
    private readonly IUserMfaKeyRepository _mfaKeyRepo;
    private readonly IUserRepository _userRepo;

    public MfaController(
        IFido2Service fido2Service,
        IUserMfaKeyRepository mfaKeyRepo,
        IUserRepository userRepo)
    {
        _fido2Service = fido2Service;
        _mfaKeyRepo = mfaKeyRepo;
        _userRepo = userRepo;
    }

    // ── Listagem ─────────────────────────────────────────────────────────────

    [HttpGet("keys")]
    public async Task<IActionResult> ListKeys()
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var keys = await _mfaKeyRepo.GetActiveByUserIdAsync(userId);
        var result = keys.Select(k => new MfaKeyDto
        {
            Id = k.Id,
            Name = k.Name,
            KeyType = k.KeyType,
            IsActive = k.IsActive,
            CreatedAt = k.CreatedAt,
            LastUsedAt = k.LastUsedAt
        });
        return Ok(result);
    }

    // ── Registro FIDO2 ────────────────────────────────────────────────────────

    /// <summary>
    /// Inicia o registro de uma nova chave de segurança FIDO2.
    /// Pode ser chamado com mfaSetupToken (primeiro setup) ou com sessão completa.
    /// </summary>
    [HttpPost("fido2/register/begin")]
    [RequireMfaSetupOrFullSession]
    public async Task<IActionResult> BeginFido2Registration()
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var user = await _userRepo.GetByIdAsync(userId);
        if (user is null) return Unauthorized();
        var existingKeys = await _mfaKeyRepo.GetActiveByUserIdAsync(userId);
        var credentialIds = existingKeys
            .Where(k => k.CredentialIdBase64 != null)
            .Select(k => k.CredentialIdBase64!);
        var optionsJson = await _fido2Service.BeginRegistrationAsync(userId, user.Email, user.FullName, credentialIds);
        return Ok(new { options = optionsJson });
    }

    /// <summary>
    /// Conclui o registro da chave FIDO2.
    /// A chave é persistida e o usuário é marcado como MFA configurado.
    /// </summary>
    [HttpPost("fido2/register/complete")]
    [RequireMfaSetupOrFullSession]
    public async Task<IActionResult> CompleteFido2Registration([FromBody] CompleteFido2RegistrationDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var result = await _fido2Service.CompleteRegistrationAsync(userId, dto.AttestationResponseJson);
        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage ?? "Falha ao registrar a chave." });

        var newKey = new UserMfaKey
        {
            Id = IdGenerator.NewId(),
            UserId = userId,
            KeyType = MfaKeyType.Fido2,
            Name = dto.KeyName.Length > 0 ? dto.KeyName : "Chave de segurança",
            IsActive = true,
            CredentialIdBase64 = result.CredentialIdBase64,
            PublicKeyBase64 = result.PublicKeyBase64,
            SignCount = result.SignCount,
            AaguidBase64 = result.AaguidBase64,
            UserHandleBase64 = result.UserHandleBase64,
            CreatedAt = DateTime.UtcNow
        };
        await _mfaKeyRepo.CreateAsync(newKey);
        await _userRepo.SetMfaConfiguredAsync(userId, true);

        return Ok(new { keyId = newKey.Id, message = "Chave registrada com sucesso." });
    }

    // ── Remoção ───────────────────────────────────────────────────────────────

    [HttpDelete("keys/{keyId:guid}")]
    public async Task<IActionResult> RemoveKey(Guid keyId)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var activeCount = await _mfaKeyRepo.CountActiveByUserIdAsync(userId);
        if (activeCount <= 1)
            return BadRequest(new { message = "Não é possível remover a única chave ativa." });

        var removed = await _mfaKeyRepo.DeactivateAsync(keyId, userId);
        return removed ? NoContent() : NotFound();
    }

    // ── Renomear ──────────────────────────────────────────────────────────────

    [HttpPatch("keys/{keyId:guid}/name")]
    public async Task<IActionResult> RenameKey(Guid keyId, [FromBody] RegisterMfaKeyNameDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var renamed = await _mfaKeyRepo.RenameAsync(keyId, userId, dto.KeyName);
        return renamed ? NoContent() : NotFound();
    }
}
