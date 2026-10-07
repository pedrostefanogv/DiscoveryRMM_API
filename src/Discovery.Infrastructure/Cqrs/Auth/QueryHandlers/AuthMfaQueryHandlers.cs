using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Auth.Queries;
using Discovery.Core.DTOs.Auth;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Auth.QueryHandlers;

public sealed class BeginFido2AssertionQueryHandler(
    IFido2Service fido2Service,
    IUserMfaKeyRepository mfaKeyRepo,
    IUserAuthService authService,
    ILogger<BeginFido2AssertionQueryHandler> logger
) : IRequestHandler<BeginFido2AssertionQuery, Result<BeginFido2AssertionResult>>
{
    public async Task<Result<BeginFido2AssertionResult>> Handle(BeginFido2AssertionQuery q, CancellationToken ct)
    {
        try
        {
            var requirement = await authService.GetEffectiveMfaRequirementAsync(q.UserId);
            if (requirement == Core.Enums.Identity.RoleMfaRequirement.Totp)
                return Result<BeginFido2AssertionResult>.Failure(Error.Forbidden("Esta conta exige MFA via OTP para login."));

            var activeKeys = await mfaKeyRepo.GetActiveByUserIdAsync(q.UserId);
            var optionsJson = await fido2Service.BeginAssertionAsync(q.UserId, activeKeys);
            return Result<BeginFido2AssertionResult>.Success(new BeginFido2AssertionResult(optionsJson));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao iniciar a asserção FIDO2 do usuário {UserId}", q.UserId);
            return Result<BeginFido2AssertionResult>.Failure(
                Error.Internal("Não foi possível iniciar a validação da chave de segurança."));
        }
    }
}

public sealed class GetFirstAccessStatusQueryHandler(
    IUserAuthService authService,
    ILogger<GetFirstAccessStatusQueryHandler> logger
) : IRequestHandler<GetFirstAccessStatusQuery, Result<FirstAccessStatusDto>>
{
    public async Task<Result<FirstAccessStatusDto>> Handle(GetFirstAccessStatusQuery q, CancellationToken ct)
    {
        try
        {
            var status = await authService.GetFirstAccessStatusAsync(q.UserId);
            return Result<FirstAccessStatusDto>.Success(status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao obter o status de primeiro acesso do usuário {UserId}", q.UserId);
            return Result<FirstAccessStatusDto>.Failure(
                Error.Internal("Não foi possível obter o status do primeiro acesso."));
        }
    }
}
