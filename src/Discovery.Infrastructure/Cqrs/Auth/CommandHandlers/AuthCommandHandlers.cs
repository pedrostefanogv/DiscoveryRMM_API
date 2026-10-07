using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.Cqrs.Auth.Queries;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Auth.CommandHandlers;

public sealed class ResetUserPasswordCommandHandler(
    IUserPasswordManagementService passwordManagement,
    IUserSessionRepository sessions,
    IUserRepository users
) : IRequestHandler<ResetUserPasswordCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ResetUserPasswordCommand cmd, CancellationToken ct)
    {
        try
        {
            await passwordManagement.ResetPasswordAsync(cmd.UserId, cmd.NewPassword, cmd.RequestedBy, ct);

            // Senha definida por um administrador é temporária: força a troca no próximo
            // login (o serviço de reset deixa MustChangePassword=false por design).
            if (await users.GetByIdAsync(cmd.UserId) is { } target)
            {
                target.MustChangePassword = true;
                await users.UpdateAsync(target);
            }

            // Sessões antigas continuariam válidas após o reset administrativo (o access
            // token é stateless e o refresh seguiria funcionando). Revogar força o uso da
            // senha nova em todos os dispositivos.
            await sessions.RevokeAllByUserIdAsync(cmd.UserId);

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
    IUserPasswordManagementService passwordManagement,
    IUserSessionRepository sessions
) : IRequestHandler<ChangeUserPasswordCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ChangeUserPasswordCommand cmd, CancellationToken ct)
    {
        try
        {
            await passwordManagement.ChangePasswordAsync(cmd.UserId, cmd.CurrentPassword, cmd.NewPassword, ct);

            // Trocar a senha invalida as outras sessões (um token roubado deixa de valer),
            // preservando a sessão que fez a troca para não deslogar o próprio usuário.
            Guid? currentSessionId = Guid.TryParse(cmd.CurrentSessionId, out var parsed) ? parsed : null;
            foreach (var session in await sessions.GetActiveByUserIdAsync(cmd.UserId))
            {
                if (currentSessionId.HasValue && session.Id == currentSessionId.Value)
                    continue;

                await sessions.RevokeAsync(session.Id);
            }

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

        // Hash do refresh token para buscar a sessão.
        // Token malformado (não-Base64) não corresponde a nenhuma sessão —
        // o logout é idempotente e não deve falhar com 500.
        byte[] refreshBytes;
        try
        {
            refreshBytes = Convert.FromBase64String(cmd.RefreshToken);
        }
        catch (FormatException)
        {
            return Result<VoidResult>.Success(VoidResult.Value);
        }
        var refreshHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(refreshBytes));

        var session = await sessionRepo.GetByRefreshTokenHashAsync(refreshHash);
        if (session is null)
            return Result<VoidResult>.Success(VoidResult.Value); // Idempotent: já revogado ou inválido

        await sessionRepo.RevokeAsync(session.Id);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
