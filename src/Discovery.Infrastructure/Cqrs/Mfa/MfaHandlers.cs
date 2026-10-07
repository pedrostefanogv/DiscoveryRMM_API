using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Mfa;
using Discovery.Core.Cqrs.Mfa.Queries;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Security;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Mfa;

public sealed class ListMfaKeysQueryHandler(IUserMfaKeyRepository repo) : IRequestHandler<ListMfaKeysQuery, Result<IReadOnlyList<MfaKeyDto>>>
{
    public async Task<Result<IReadOnlyList<MfaKeyDto>>> Handle(ListMfaKeysQuery q, CancellationToken ct)
    {
        var keys = await repo.GetActiveByUserIdAsync(q.UserId);
        var items = keys.Select(k => new MfaKeyDto(k.Id, k.UserId, (int)k.KeyType, k.Name, k.IsActive, k.CreatedAt, k.LastUsedAt)).ToList().AsReadOnly();
        return Result<IReadOnlyList<MfaKeyDto>>.Success(items);
    }
}

public sealed class BeginFido2RegistrationQueryHandler(
    IFido2Service fido2Service,
    IUserRepository userRepo,
    IUserMfaKeyRepository mfaKeyRepo,
    ILogger<BeginFido2RegistrationQueryHandler> logger
) : IRequestHandler<BeginFido2RegistrationQuery, Result<BeginFido2RegistrationResult>>
{
    public async Task<Result<BeginFido2RegistrationResult>> Handle(BeginFido2RegistrationQuery q, CancellationToken ct)
    {
        try
        {
            var user = await userRepo.GetByIdAsync(q.UserId);
            if (user is null)
                return Result<BeginFido2RegistrationResult>.Failure(Error.NotFound("Usuário não encontrado."));

            var activeKeys = await mfaKeyRepo.GetActiveByUserIdAsync(q.UserId);
            var existingCredentialIds = activeKeys
                .Where(k => k.KeyType == MfaKeyType.Fido2 && !string.IsNullOrWhiteSpace(k.CredentialIdBase64))
                .Select(k => k.CredentialIdBase64!)
                .ToList();

            var optionsJson = await fido2Service.BeginRegistrationAsync(
                q.UserId, user.Email, user.FullName, existingCredentialIds);

            return Result<BeginFido2RegistrationResult>.Success(new BeginFido2RegistrationResult(optionsJson));
        }
        catch (Exception ex)
        {
            // Antes a ex.Message (interna) era devolvida ao cliente. Agora só é logada.
            logger.LogError(ex, "Falha ao iniciar registro FIDO2 para o usuário {UserId}", q.UserId);
            return Result<BeginFido2RegistrationResult>.Failure(
                Error.Internal("Não foi possível iniciar o registro da chave de segurança."));
        }
    }
}

public sealed class CompleteFido2RegistrationCommandHandler(
    IFido2Service fido2Service,
    IUserMfaKeyRepository mfaKeyRepo,
    IUserRepository userRepo,
    ILogger<CompleteFido2RegistrationCommandHandler> logger
) : IRequestHandler<CompleteFido2RegistrationCommand, Result<CompleteFido2RegistrationResult>>
{
    public async Task<Result<CompleteFido2RegistrationResult>> Handle(CompleteFido2RegistrationCommand cmd, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cmd.AttestationResponseJson))
                return Result<CompleteFido2RegistrationResult>.Failure(Error.Validation("attestationResponseJson", "Resposta de attestation é obrigatoria."));

            var keyName = cmd.KeyName?.Trim();
            if (string.IsNullOrWhiteSpace(keyName) || keyName.Length < 2 || keyName.Length > 80)
                return Result<CompleteFido2RegistrationResult>.Failure(Error.Validation("keyName", "Informe um nome entre 2 e 80 caracteres."));

            var result = await fido2Service.CompleteRegistrationAsync(cmd.UserId, cmd.AttestationResponseJson);
            if (!result.Success)
                return Result<CompleteFido2RegistrationResult>.Failure(Error.Unauthorized(result.ErrorMessage ?? "Registro FIDO2 inválido."));

            var key = new UserMfaKey
            {
                Id = IdGenerator.NewId(),
                UserId = cmd.UserId,
                KeyType = MfaKeyType.Fido2,
                Name = keyName,
                IsActive = true,
                CredentialIdBase64 = result.CredentialIdBase64,
                PublicKeyBase64 = result.PublicKeyBase64,
                SignCount = result.SignCount,
                AaguidBase64 = result.AaguidBase64,
                UserHandleBase64 = result.UserHandleBase64,
                CreatedAt = DateTime.UtcNow
            };

            var created = await mfaKeyRepo.CreateAsync(key);
            await userRepo.SetMfaConfiguredAsync(cmd.UserId, true);

            return Result<CompleteFido2RegistrationResult>.Success(
                new CompleteFido2RegistrationResult(created.Id, "Chave FIDO2 registrada com sucesso."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao concluir registro FIDO2 para o usuário {UserId}", cmd.UserId);
            return Result<CompleteFido2RegistrationResult>.Failure(
                Error.Internal("Não foi possível concluir o registro da chave de segurança."));
        }
    }
}

/// <summary>
/// Início do cadastro de TOTP. O segredo é devolvido ao usuário (QR/URI) e reenviado no
/// complete — a validação do código acontece contra esse mesmo segredo.
/// </summary>
public sealed class BeginTotpRegistrationQueryHandler(
    IOtpService otpService,
    IUserRepository userRepo,
    IConfiguration configuration,
    ILogger<BeginTotpRegistrationQueryHandler> logger
) : IRequestHandler<BeginTotpRegistrationQuery, Result<BeginTotpRegistrationResult>>
{
    private const string DefaultIssuer = "Discovery";

    public async Task<Result<BeginTotpRegistrationResult>> Handle(BeginTotpRegistrationQuery q, CancellationToken ct)
    {
        try
        {
            var user = await userRepo.GetByIdAsync(q.UserId);
            if (user is null)
                return Result<BeginTotpRegistrationResult>.Failure(Error.NotFound("Usuário não encontrado."));

            var issuer = configuration.GetValue<string>("Authentication:Mfa:Issuer") ?? DefaultIssuer;
            var account = string.IsNullOrWhiteSpace(user.Email) ? user.Login : user.Email;

            var (secretBase32, qrCodeUri) = otpService.GenerateSecret(issuer, account);

            return Result<BeginTotpRegistrationResult>.Success(new BeginTotpRegistrationResult(
                secretBase32,
                qrCodeUri,
                "Escaneie o QR code (ou informe o segredo) no aplicativo autenticador e confirme o código de 6 dígitos."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao iniciar o registro TOTP do usuário {UserId}", q.UserId);
            return Result<BeginTotpRegistrationResult>.Failure(
                Error.Internal("Não foi possível iniciar o registro do OTP."));
        }
    }
}

public sealed class CompleteTotpRegistrationCommandHandler(
    IOtpService otpService,
    ISecretProtector secretProtector,
    IUserMfaKeyRepository mfaKeyRepo,
    IUserRepository userRepo,
    ILogger<CompleteTotpRegistrationCommandHandler> logger
) : IRequestHandler<CompleteTotpRegistrationCommand, Result<CompleteTotpRegistrationResult>>
{
    private const int BackupCodeCount = 8;

    public async Task<Result<CompleteTotpRegistrationResult>> Handle(CompleteTotpRegistrationCommand cmd, CancellationToken ct)
    {
        try
        {
            var secret = cmd.SecretBase32?.Trim() ?? string.Empty;
            if (secret.Length == 0)
                return Result<CompleteTotpRegistrationResult>.Failure(
                    Error.Validation("secretBase32", "Segredo OTP é obrigatório."));

            var keyName = cmd.KeyName?.Trim() ?? string.Empty;
            if (keyName.Length < 2 || keyName.Length > 80)
                return Result<CompleteTotpRegistrationResult>.Failure(
                    Error.Validation("keyName", "Informe um nome entre 2 e 80 caracteres."));

            var code = new string((cmd.VerificationCode ?? string.Empty).Where(char.IsDigit).ToArray());
            if (code.Length < 6)
                return Result<CompleteTotpRegistrationResult>.Failure(
                    Error.Validation("verificationCode", "Informe o código de verificação com 6 dígitos."));

            if (await userRepo.GetByIdAsync(cmd.UserId) is null)
                return Result<CompleteTotpRegistrationResult>.Failure(Error.NotFound("Usuário não encontrado."));

            if (!otpService.ValidateTotp(secret, code))
                return Result<CompleteTotpRegistrationResult>.Failure(
                    Error.Validation("verificationCode", "Código inválido. Confira o horário do dispositivo e tente novamente."));

            var (plaintextCodes, hashedCodes) = otpService.GenerateBackupCodes(BackupCodeCount);

            var key = new UserMfaKey
            {
                Id = IdGenerator.NewId(),
                UserId = cmd.UserId,
                KeyType = MfaKeyType.Totp,
                Name = keyName,
                IsActive = true,
                OtpSecretEncrypted = secretProtector.Protect(secret),
                BackupCodeHashes = hashedCodes.ToArray(),
                CreatedAt = DateTime.UtcNow
            };

            var created = await mfaKeyRepo.CreateAsync(key);
            await userRepo.SetMfaConfiguredAsync(cmd.UserId, true);

            logger.LogInformation("Chave TOTP {KeyId} registrada para o usuário {UserId}", created.Id, cmd.UserId);

            return Result<CompleteTotpRegistrationResult>.Success(new CompleteTotpRegistrationResult(
                "Chave OTP registrada com sucesso. Guarde os códigos de backup em local seguro.",
                plaintextCodes.ToList()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao concluir o registro TOTP do usuário {UserId}", cmd.UserId);
            return Result<CompleteTotpRegistrationResult>.Failure(
                Error.Internal("Não foi possível concluir o registro do OTP."));
        }
    }
}

public sealed class RenameMfaKeyCommandHandler(
    IUserMfaKeyRepository mfaKeyRepo,
    ILogger<RenameMfaKeyCommandHandler> logger
) : IRequestHandler<RenameMfaKeyCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RenameMfaKeyCommand cmd, CancellationToken ct)
    {
        try
        {
            var keyName = cmd.KeyName?.Trim();
            if (string.IsNullOrWhiteSpace(keyName) || keyName.Length < 2 || keyName.Length > 80)
                return Result<VoidResult>.Failure(Error.Validation("keyName", "Informe um nome entre 2 e 80 caracteres."));

            var ok = await mfaKeyRepo.RenameAsync(cmd.KeyId, cmd.UserId, keyName);
            return ok
                ? Result<VoidResult>.Success(VoidResult.Value)
                : Result<VoidResult>.Failure(Error.NotFound("Chave MFA não encontrada."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao renomear a chave MFA {KeyId} do usuário {UserId}", cmd.KeyId, cmd.UserId);
            return Result<VoidResult>.Failure(Error.Internal("Não foi possível renomear a chave."));
        }
    }
}

public sealed class DeleteMfaKeyCommandHandler(
    IUserMfaKeyRepository mfaKeyRepo,
    ILogger<DeleteMfaKeyCommandHandler> logger
) : IRequestHandler<DeleteMfaKeyCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteMfaKeyCommand cmd, CancellationToken ct)
    {
        try
        {
            var ok = await mfaKeyRepo.DeactivateAsync(cmd.KeyId, cmd.UserId);
            return ok
                ? Result<VoidResult>.Success(VoidResult.Value)
                : Result<VoidResult>.Failure(Error.NotFound("Chave MFA não encontrada."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao remover a chave MFA {KeyId} do usuário {UserId}", cmd.KeyId, cmd.UserId);
            return Result<VoidResult>.Failure(Error.Internal("Não foi possível remover a chave."));
        }
    }
}
