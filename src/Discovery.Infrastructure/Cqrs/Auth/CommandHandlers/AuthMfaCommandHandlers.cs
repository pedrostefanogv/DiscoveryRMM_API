using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.DTOs.Auth;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Security;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Auth.CommandHandlers;

public sealed class CompleteFido2AssertionCommandHandler(
    IFido2Service fido2Service,
    IUserAuthService authService,
    IUserMfaKeyRepository mfaKeyRepo
) : IRequestHandler<CompleteFido2AssertionCommand, Result<TokenPairDto>>
{
    public async Task<Result<TokenPairDto>> Handle(CompleteFido2AssertionCommand cmd, CancellationToken ct)
    {
        try
        {
            var requirement = await authService.GetEffectiveMfaRequirementAsync(cmd.UserId);
            if (requirement == Core.Enums.Identity.RoleMfaRequirement.Totp)
                return Result<TokenPairDto>.Failure(Error.Forbidden("Esta conta exige MFA via OTP para login."));

            var activeKeys = await mfaKeyRepo.GetActiveByUserIdAsync(cmd.UserId);
            var result = await fido2Service.CompleteAssertionAsync(cmd.UserId, cmd.AssertionResponseJson, activeKeys);
            if (!result.Success)
                return Result<TokenPairDto>.Failure(Error.Unauthorized(result.ErrorMessage ?? "MFA inválido."));

            await mfaKeyRepo.UpdateSignCountAsync(result.KeyId, result.NewSignCount);
            await mfaKeyRepo.UpdateLastUsedAsync(result.KeyId);

            var session = await authService.IssueFullSessionAsync(cmd.UserId, true, cmd.IpAddress, cmd.UserAgent);
            return Result<TokenPairDto>.Success(session);
        }
        catch (Exception ex)
        {
            return Result<TokenPairDto>.Failure(Error.Internal(ex.Message));
        }
    }
}

public sealed class CompleteOtpAssertionCommandHandler(
    IOtpService otpService,
    IUserAuthService authService,
    IUserMfaKeyRepository mfaKeyRepo,
    ISecretProtector secretProtector
) : IRequestHandler<CompleteOtpAssertionCommand, Result<TokenPairDto>>
{
    public async Task<Result<TokenPairDto>> Handle(CompleteOtpAssertionCommand cmd, CancellationToken ct)
    {
        try
        {
            var requirement = await authService.GetEffectiveMfaRequirementAsync(cmd.UserId);
            if (requirement == Core.Enums.Identity.RoleMfaRequirement.Fido2)
                return Result<TokenPairDto>.Failure(Error.Forbidden("Esta conta exige MFA via chave de segurança (FIDO2)."));

            if (string.IsNullOrWhiteSpace(cmd.Code))
                return Result<TokenPairDto>.Failure(Error.Validation("Code", "Código OTP é obrigatório."));

            var activeKeys = await mfaKeyRepo.GetActiveByUserIdAsync(cmd.UserId);
            var otpKeys = activeKeys
                .Where(k => k.KeyType == MfaKeyType.Totp && !string.IsNullOrWhiteSpace(k.OtpSecretEncrypted))
                .ToList();

            if (otpKeys.Count == 0)
                return Result<TokenPairDto>.Failure(Error.Unauthorized("Nenhuma credencial OTP ativa encontrada."));

            var normalizedCode = cmd.Code.Trim();
            UserMfaKey? matchedKey = null;
            foreach (var key in otpKeys)
            {
                var secret = secretProtector.UnprotectOrSelf(key.OtpSecretEncrypted);
                if (otpService.ValidateTotp(secret, normalizedCode))
                {
                    matchedKey = key;
                    break;
                }
            }

            if (matchedKey is null)
                return Result<TokenPairDto>.Failure(Error.Unauthorized("OTP inválido."));

            await mfaKeyRepo.UpdateLastUsedAsync(matchedKey.Id);

            var session = await authService.IssueFullSessionAsync(cmd.UserId, true, cmd.IpAddress, cmd.UserAgent);
            return Result<TokenPairDto>.Success(session);
        }
        catch (Exception ex)
        {
            return Result<TokenPairDto>.Failure(Error.Internal(ex.Message));
        }
    }
}

public sealed class CompleteFirstAccessCommandHandler(
    IUserAuthService authService
) : IRequestHandler<CompleteFirstAccessCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(CompleteFirstAccessCommand cmd, CancellationToken ct)
    {
        try
        {
            await authService.CompleteFirstAccessAsync(cmd.UserId, cmd.Dto);
            return Result<VoidResult>.Success(VoidResult.Value);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<VoidResult>.Failure(Error.Unauthorized(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Result<VoidResult>.Failure(Error.Validation("Dto", ex.Message));
        }
    }
}
