using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.DTOs.Auth;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Security;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Auth.CommandHandlers;

/// <summary>
/// Guarda de tentativas de MFA por conta.
///
/// Motivação: o lockout de senha não cobria o segundo fator. Com a senha correta, um
/// atacante podia reautenticar indefinidamente e forçar o código OTP de 6 dígitos
/// limitado apenas pelo rate limit por IP (20 req/min). Os limiares espelham a política
/// já usada no login: 5 → 60s, 10 → 300s, 20+ → 1800s.
/// </summary>
internal static class MfaAttemptGuard
{
    internal static string? DescribeLockout(User user)
    {
        if (user.MfaLockoutUntil is not { } until || until <= DateTime.UtcNow)
            return null;

        var remainingSeconds = Math.Max(1, (int)(until - DateTime.UtcNow).TotalSeconds);
        return $"Muitas tentativas de MFA. Tente novamente em {remainingSeconds} segundos.";
    }

    internal static async Task RegisterFailureAsync(
        IUserRepository users,
        User user,
        ILogger logger,
        Guid userId)
    {
        user.MfaFailedAttempts++;
        user.MfaLockoutUntil = user.MfaFailedAttempts switch
        {
            >= 20 => DateTime.UtcNow.AddSeconds(1800),
            >= 10 => DateTime.UtcNow.AddSeconds(300),
            >= 5 => DateTime.UtcNow.AddSeconds(60),
            _ => null
        };

        // Somente os contadores são persistidos (evita sobrescrever alterações concorrentes
        // no usuário, como uma troca de senha).
        await users.SetMfaFailureStateAsync(userId, user.MfaFailedAttempts, user.MfaLockoutUntil);

        logger.LogWarning(
            "Falha de MFA para o usuário {UserId}: tentativa {Attempts}, bloqueado até {LockoutUntil}",
            userId, user.MfaFailedAttempts, user.MfaLockoutUntil);
    }

    internal static async Task ResetAsync(IUserRepository users, User user)
    {
        if (user.MfaFailedAttempts == 0 && user.MfaLockoutUntil is null)
            return;

        user.MfaFailedAttempts = 0;
        user.MfaLockoutUntil = null;
        await users.ResetMfaFailureStateAsync(user.Id);
    }
}

/// <summary>
/// Uso único do token mfa_pending (claim jti). O consumo acontece apenas quando o
/// segundo fator é validado — assim uma tentativa com código errado pode ser repetida.
/// Se o Redis estiver indisponível a checagem é liberada (fail-open) com log de aviso:
/// bloquear o login por indisponibilidade de cache seria pior que o risco mitigado.
/// </summary>
internal static class MfaTokenUsage
{
    private const string KeyPrefix = "mfa:pending:used:";
    private const int TtlSeconds = 900;

    internal static async Task<bool> IsConsumedAsync(IRedisService redis, ILogger logger, string? tokenId)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
            return false;

        try
        {
            return await redis.GetAsync(KeyPrefix + tokenId) is not null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível verificar o uso do token de MFA; seguindo sem a checagem.");
            return false;
        }
    }

    internal static async Task MarkConsumedAsync(IRedisService redis, ILogger logger, string? tokenId)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
            return;

        try
        {
            await redis.SetIfNotExistsAsync(KeyPrefix + tokenId, "1", TtlSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível registrar o uso do token de MFA.");
        }
    }
}

public sealed class CompleteFido2AssertionCommandHandler(
    IFido2Service fido2Service,
    IUserAuthService authService,
    IUserMfaKeyRepository mfaKeyRepo,
    IUserRepository users,
    IRedisService redis,
    ILogger<CompleteFido2AssertionCommandHandler> logger
) : IRequestHandler<CompleteFido2AssertionCommand, Result<TokenPairDto>>
{
    public async Task<Result<TokenPairDto>> Handle(CompleteFido2AssertionCommand cmd, CancellationToken ct)
    {
        try
        {
            var user = await users.GetByIdAsync(cmd.UserId);
            if (user is null)
                return Result<TokenPairDto>.Failure(Error.Unauthorized("Usuário não encontrado."));

            // 429: o console não deve tratar isso como token de MFA expirado.
            if (MfaAttemptGuard.DescribeLockout(user) is { } lockoutMessage)
                return Result<TokenPairDto>.Failure(Error.TooManyRequests(lockoutMessage));

            if (await MfaTokenUsage.IsConsumedAsync(redis, logger, cmd.MfaTokenId))
                return Result<TokenPairDto>.Failure(
                    Error.Unauthorized("Este desafio de MFA já foi utilizado. Faça login novamente."));

            var requirement = await authService.GetEffectiveMfaRequirementAsync(cmd.UserId);
            if (requirement == Core.Enums.Identity.RoleMfaRequirement.Totp)
                return Result<TokenPairDto>.Failure(Error.Forbidden("Esta conta exige MFA via OTP para login."));

            var activeKeys = await mfaKeyRepo.GetActiveByUserIdAsync(cmd.UserId);
            var result = await fido2Service.CompleteAssertionAsync(cmd.UserId, cmd.AssertionResponseJson, activeKeys);
            if (!result.Success)
            {
                await MfaAttemptGuard.RegisterFailureAsync(users, user, logger, cmd.UserId);
                return Result<TokenPairDto>.Failure(Error.Unauthorized(result.ErrorMessage ?? "MFA inválido."));
            }

            await mfaKeyRepo.UpdateSignCountAsync(result.KeyId, result.NewSignCount);
            await mfaKeyRepo.UpdateLastUsedAsync(result.KeyId);
            await MfaAttemptGuard.ResetAsync(users, user);
            await MfaTokenUsage.MarkConsumedAsync(redis, logger, cmd.MfaTokenId);

            var session = await authService.IssueFullSessionAsync(cmd.UserId, true, cmd.IpAddress, cmd.UserAgent);
            return Result<TokenPairDto>.Success(session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro inesperado ao concluir a asserção FIDO2 do usuário {UserId}", cmd.UserId);
            return Result<TokenPairDto>.Failure(
                Error.Internal("Não foi possível concluir a validação da chave de segurança."));
        }
    }
}

public sealed class CompleteOtpAssertionCommandHandler(
    IOtpService otpService,
    IUserAuthService authService,
    IUserMfaKeyRepository mfaKeyRepo,
    IUserRepository users,
    IRedisService redis,
    ISecretProtector secretProtector,
    ILogger<CompleteOtpAssertionCommandHandler> logger
) : IRequestHandler<CompleteOtpAssertionCommand, Result<TokenPairDto>>
{
    public async Task<Result<TokenPairDto>> Handle(CompleteOtpAssertionCommand cmd, CancellationToken ct)
    {
        try
        {
            var user = await users.GetByIdAsync(cmd.UserId);
            if (user is null)
                return Result<TokenPairDto>.Failure(Error.Unauthorized("Usuário não encontrado."));

            // 429: o console não deve tratar isso como token de MFA expirado.
            if (MfaAttemptGuard.DescribeLockout(user) is { } lockoutMessage)
                return Result<TokenPairDto>.Failure(Error.TooManyRequests(lockoutMessage));

            if (await MfaTokenUsage.IsConsumedAsync(redis, logger, cmd.MfaTokenId))
                return Result<TokenPairDto>.Failure(
                    Error.Unauthorized("Este desafio de MFA já foi utilizado. Faça login novamente."));

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
            long matchedStep = 0;
            string? consumedBackupCodeHash = null;

            foreach (var key in otpKeys)
            {
                var secret = secretProtector.UnprotectOrSelf(key.OtpSecretEncrypted);
                // minStepExclusive = último passo consumido nesta chave: rejeita replay do
                // mesmo código (ou de um código anterior) dentro da janela de tolerância.
                if (otpService.TryValidateTotp(secret, normalizedCode, key.LastUsedStep, out var step))
                {
                    matchedKey = key;
                    matchedStep = step;
                    break;
                }
            }

            if (matchedKey is null)
            {
                // Fallback: código de backup (uso único) — recuperação quando o autenticador
                // foi perdido. Antes os códigos eram gerados/armazenados mas nunca aceitos.
                foreach (var key in otpKeys)
                {
                    var hashes = key.BackupCodeHashes;
                    if (hashes is null || hashes.Length == 0)
                        continue;

                    if (otpService.VerifyBackupCode(normalizedCode, hashes, out var matchedHash)
                        && matchedHash is not null)
                    {
                        matchedKey = key;
                        consumedBackupCodeHash = matchedHash;
                        break;
                    }
                }
            }

            if (matchedKey is null)
            {
                await MfaAttemptGuard.RegisterFailureAsync(users, user, logger, cmd.UserId);
                return Result<TokenPairDto>.Failure(Error.Unauthorized("OTP inválido."));
            }

            if (consumedBackupCodeHash is not null)
            {
                var remaining = (matchedKey.BackupCodeHashes ?? [])
                    .Where(h => !string.Equals(h, consumedBackupCodeHash, StringComparison.Ordinal))
                    .ToArray();

                await mfaKeyRepo.UpdateBackupCodeHashesAsync(matchedKey.Id, remaining);
                logger.LogWarning(
                    "Login por código de backup do usuário {UserId}: restam {Remaining} código(s) na chave {KeyId}.",
                    cmd.UserId, remaining.Length, matchedKey.Id);
            }
            else
            {
                await mfaKeyRepo.SetLastUsedStepAsync(matchedKey.Id, matchedStep);
            }

            await mfaKeyRepo.UpdateLastUsedAsync(matchedKey.Id);
            await MfaAttemptGuard.ResetAsync(users, user);
            await MfaTokenUsage.MarkConsumedAsync(redis, logger, cmd.MfaTokenId);

            var session = await authService.IssueFullSessionAsync(cmd.UserId, true, cmd.IpAddress, cmd.UserAgent);
            return Result<TokenPairDto>.Success(session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro inesperado ao concluir a asserção OTP do usuário {UserId}", cmd.UserId);
            return Result<TokenPairDto>.Failure(Error.Internal("Não foi possível validar o código OTP."));
        }
    }
}

public sealed class CompleteFirstAccessCommandHandler(
    IUserAuthService authService,
    ILogger<CompleteFirstAccessCommandHandler> logger
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro inesperado ao concluir o primeiro acesso do usuário {UserId}", cmd.UserId);
            return Result<VoidResult>.Failure(Error.Internal("Não foi possível concluir o primeiro acesso."));
        }
    }
}
