using Discovery.Core.DTOs.Mfa;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Security;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/mfa")]
public class MfaController : ControllerBase
{
    private readonly IFido2Service _fido2Service;
    private readonly IOtpService _otpService;
    private readonly IUserMfaKeyRepository _mfaKeyRepo;
    private readonly IUserRepository _userRepo;
    private readonly ISecretProtector _secretProtector;

    public MfaController(
        IFido2Service fido2Service,
        IOtpService otpService,
        IUserMfaKeyRepository mfaKeyRepo,
        IUserRepository userRepo,
        ISecretProtector secretProtector)
    {
        _fido2Service = fido2Service;
        _otpService = otpService;
        _mfaKeyRepo = mfaKeyRepo;
        _userRepo = userRepo;
        _secretProtector = secretProtector;
    }

    // ── Listagem ─────────────────────────────────────────────────────────────

    [HttpGet("keys")]
    [RequireUserAuth]
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
    [AllowAnonymous]
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
    [AllowAnonymous]
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

    // ── Registro TOTP ───────────────────────────────────────────────────────

    /// <summary>
    /// Inicia o registro de um autenticador OTP/TOTP.
    /// </summary>
    [HttpPost("totp/register/begin")]
    [AllowAnonymous]
    [RequireMfaSetupOrFullSession]
    public async Task<IActionResult> BeginTotpRegistration()
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var user = await _userRepo.GetByIdAsync(userId);
        if (user is null) return Unauthorized();

        var accountName = string.IsNullOrWhiteSpace(user.Login) ? user.Email : user.Login;
        var (secretBase32, qrCodeUri) = _otpService.GenerateSecret("Discovery", accountName);

        return Ok(new
        {
            secretBase32,
            qrCodeUri,
            message = "Use o QR Code em um app autenticador e confirme com um código OTP."
        });
    }

    /// <summary>
    /// Conclui o registro de um autenticador OTP/TOTP.
    /// </summary>
    [HttpPost("totp/register/complete")]
    [AllowAnonymous]
    [RequireMfaSetupOrFullSession]
    public async Task<IActionResult> CompleteTotpRegistration([FromBody] CompleteTotpRegistrationDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;

        if (string.IsNullOrWhiteSpace(dto.SecretBase32) || string.IsNullOrWhiteSpace(dto.VerificationCode))
            return BadRequest(new { message = "SecretBase32 e VerificationCode são obrigatórios." });

        var valid = _otpService.ValidateTotp(dto.SecretBase32.Trim(), dto.VerificationCode.Trim());
        if (!valid)
            return BadRequest(new { message = "Código OTP inválido para o segredo informado." });

        var (backupCodes, backupHashes) = _otpService.GenerateBackupCodes();

        var newKey = new UserMfaKey
        {
            Id = IdGenerator.NewId(),
            UserId = userId,
            KeyType = MfaKeyType.Totp,
            Name = string.IsNullOrWhiteSpace(dto.KeyName) ? "Authenticator OTP" : dto.KeyName.Trim(),
            IsActive = true,
            OtpSecretEncrypted = _secretProtector.Protect(dto.SecretBase32.Trim()),
            BackupCodeHashes = backupHashes.ToArray(),
            CreatedAt = DateTime.UtcNow
        };

        await _mfaKeyRepo.CreateAsync(newKey);
        await _userRepo.SetMfaConfiguredAsync(userId, true);

        return Ok(new
        {
            keyId = newKey.Id,
            message = "Autenticador OTP registrado com sucesso.",
            backupCodes
        });
    }

    // ── Remoção ───────────────────────────────────────────────────────────────

    [HttpDelete("keys/{keyId:guid}")]
    [RequireUserAuth]
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
    [RequireUserAuth]
    public async Task<IActionResult> RenameKey(Guid keyId, [FromBody] RegisterMfaKeyNameDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var renamed = await _mfaKeyRepo.RenameAsync(keyId, userId, dto.KeyName);
        return renamed ? NoContent() : NotFound();
    }
}
