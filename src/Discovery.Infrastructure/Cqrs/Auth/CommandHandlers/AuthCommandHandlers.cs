using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.Cqrs.Auth.Queries;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Auth.CommandHandlers;

public sealed class ResetUserPasswordCommandHandler(
    IUserPasswordManagementService passwordManagement
) : IRequestHandler<ResetUserPasswordCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ResetUserPasswordCommand cmd, CancellationToken ct)
    {
        try
        {
            await passwordManagement.ResetPasswordAsync(cmd.UserId, cmd.NewPassword, cmd.RequestedBy, ct);
            return Result<VoidResult>.Success(VoidResult.Value);
        }
        catch (KeyNotFoundException)
        {
            return Result<VoidResult>.Failure(Error.NotFound($"User {cmd.UserId} not found"));
        }
        catch (ArgumentException ex)
        {
            return Result<VoidResult>.Failure(Error.Validation("NewPassword", ex.Message));
        }
    }
}

public sealed class ChangeUserPasswordCommandHandler(
    IUserPasswordManagementService passwordManagement
) : IRequestHandler<ChangeUserPasswordCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ChangeUserPasswordCommand cmd, CancellationToken ct)
    {
        try
        {
            await passwordManagement.ChangePasswordAsync(cmd.UserId, cmd.CurrentPassword, cmd.NewPassword, ct);
            return Result<VoidResult>.Success(VoidResult.Value);
        }
        catch (KeyNotFoundException)
        {
            return Result<VoidResult>.Failure(Error.NotFound($"User {cmd.UserId} not found"));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<VoidResult>.Failure(Error.Validation("CurrentPassword", "Current password is incorrect"));
        }
        catch (ArgumentException ex)
        {
            return Result<VoidResult>.Failure(Error.Validation("NewPassword", ex.Message));
        }
    }
}

public sealed class LogoutCommandHandler(
    IUserSessionRepository sessionRepo
) : IRequestHandler<LogoutCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(LogoutCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.RefreshToken))
            return Result<VoidResult>.Failure(Error.Validation("RefreshToken", "Refresh token is required for logout"));

        // Hash do refresh token para buscar a sessão
        var refreshBytes = Convert.FromBase64String(cmd.RefreshToken);
        var refreshHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(refreshBytes));

        var session = await sessionRepo.GetByRefreshTokenHashAsync(refreshHash);
        if (session is null)
            return Result<VoidResult>.Success(VoidResult.Value); // Idempotent: já revogado ou inválido

        await sessionRepo.RevokeAsync(session.Id);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ValidateOtpCommandHandler(
) : IRequestHandler<ValidateOtpCommand, Result<ValidateOtpResult>>
{
    public async Task<Result<ValidateOtpResult>> Handle(ValidateOtpCommand cmd, CancellationToken ct)
    {
        // OTP validation: o comando contém apenas o código digitado pelo usuário (6 dígitos).
        // O secret TOTP está armazenado em UserMfaKey.OtpSecretEncrypted e precisa ser
        // desencriptado antes da validação, o que requer IUserMfaKeyRepository + ISecretProtector.
        // A orquestração completa (buscar chave TOTP do usuário, desencriptar, validar código)
        // deve ser feita no MfaController (a ser migrado na Fase 3.3).
        //
        // CORREÇÃO DO BUG: o código antigo passava `cmd.OtpCode` como secret:
        //   otpService.ValidateTotp(cmd.OtpCode, cmd.OtpCode) — isso sempre falharia.
        // Agora o handler indica que o fluxo requer orquestração completa do controller.
        return Result<ValidateOtpResult>.Failure(
            Error.Validation("OtpCode", "OTP validation requires full user context. Use MfaController orchestration."));
    }
}