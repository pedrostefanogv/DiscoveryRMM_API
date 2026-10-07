using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.DTOs.Auth;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Enums.Security;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Cqrs.Auth.CommandHandlers;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OtpNet;
using System.Text;

namespace Discovery.Tests;

/// <summary>
/// Regras de segurança do segundo fator adicionadas na correção dos residuais:
/// - código de backup (uso único) aceito no login e consumido;
/// - token mfa_pending de uso único (replay do desafio recusado);
/// - lockout de MFA devolvido como "TooManyRequests" (429 no controller).
/// </summary>
[TestFixture]
public class OtpAssertionHandlerTests
{
    private static readonly OtpAuthService Otp = new();
    private static readonly FakeSecretProtector Protector = new();

    [Test]
    public async Task BackupCode_IsAccepted_AndConsumedOnce()
    {
        var user = NewUser();
        var (secret, _) = Otp.GenerateSecret("Discovery", user.Email);
        var (plaintextCodes, hashedCodes) = Otp.GenerateBackupCodes(2);

        var key = NewTotpKey(user.Id, secret, hashedCodes.ToArray());
        var keys = new FakeMfaKeyRepository(key);
        var handler = NewHandler(user, keys, new FakeRedisService());

        var first = await handler.Handle(
            new CompleteOtpAssertionCommand(user.Id, plaintextCodes.First(), null, null), CancellationToken.None);

        Assert.That(first.IsSuccess, Is.True, "código de backup deve autenticar");
        Assert.That(key.BackupCodeHashes, Has.Length.EqualTo(1), "o código usado deve ser consumido");

        // O código consumido não funciona de novo.
        var second = await handler.Handle(
            new CompleteOtpAssertionCommand(user.Id, plaintextCodes.First(), null, null), CancellationToken.None);

        Assert.That(second.IsFailure, Is.True);
        Assert.That(second.Errors[0].Code, Is.EqualTo("Unauthorized"));
    }

    [Test]
    public async Task PendingMfaToken_CannotBeReplayed()
    {
        var user = NewUser();
        var (secret, _) = Otp.GenerateSecret("Discovery", user.Email);
        var (_, hashedCodes) = Otp.GenerateBackupCodes(2);
        var key = NewTotpKey(user.Id, secret, hashedCodes.ToArray());

        var redis = new FakeRedisService();
        var handler = NewHandler(user, new FakeMfaKeyRepository(key), redis);
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

        var first = await handler.Handle(
            new CompleteOtpAssertionCommand(user.Id, code, null, null, "desafio-1"), CancellationToken.None);
        Assert.That(first.IsSuccess, Is.True);

        var replay = await handler.Handle(
            new CompleteOtpAssertionCommand(user.Id, code, null, null, "desafio-1"), CancellationToken.None);

        Assert.That(replay.IsFailure, Is.True);
        Assert.That(replay.Errors[0].Message, Does.Contain("já foi utilizado"));
    }

    [Test]
    public async Task LockedMfaAccount_ReturnsTooManyRequestsCode()
    {
        var user = NewUser();
        user.MfaLockoutUntil = DateTime.UtcNow.AddMinutes(5);
        var handler = NewHandler(user, new FakeMfaKeyRepository(), new FakeRedisService());

        var result = await handler.Handle(
            new CompleteOtpAssertionCommand(user.Id, "123456", null, null), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("TooManyRequests"));
    }

    private static CompleteOtpAssertionCommandHandler NewHandler(
        User user, FakeMfaKeyRepository keys, FakeRedisService redis)
        => new(
            Otp, new FakeAuthService(), keys, new FakeUserRepository(user), redis, Protector,
            NullLogger<CompleteOtpAssertionCommandHandler>.Instance);

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        Login = "ana",
        Email = "ana@empresa.com",
        FullName = "Ana",
        PasswordHash = "h",
        PasswordSalt = "s",
        IsActive = true,
        MfaRequired = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static UserMfaKey NewTotpKey(Guid userId, string secret, string[] backupHashes) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        KeyType = MfaKeyType.Totp,
        Name = "Authenticator",
        IsActive = true,
        OtpSecretEncrypted = Protector.Protect(secret),
        BackupCodeHashes = backupHashes,
        CreatedAt = DateTime.UtcNow
    };

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public bool IsEnabled => true;
        public bool IsProtected(string? value) => value?.StartsWith("enc:") == true;
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => protectedValue.StartsWith("enc:")
            ? Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[4..]))
            : protectedValue;
        public string UnprotectOrSelf(string? value) => value is null ? string.Empty : Unprotect(value);
    }

    private sealed class FakeRedisService : IRedisService
    {
        private readonly Dictionary<string, string> _values = new();
        public bool IsConnected => true;
        public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(1L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(amount);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) { _values[key] = value; return Task.CompletedTask; }
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(true);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(0);
        public Task DeleteAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds)
        {
            if (_values.ContainsKey(key)) return Task.FromResult(false);
            _values[key] = value;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeMfaKeyRepository(UserMfaKey? key = null) : IUserMfaKeyRepository
    {
        public Task<IEnumerable<UserMfaKey>> GetActiveByUserIdAsync(Guid userId)
            => Task.FromResult<IEnumerable<UserMfaKey>>(key is null ? [] : [key]);

        public Task<bool> SetLastUsedStepAsync(Guid keyId, long step)
        {
            if (key is not null) key.LastUsedStep = step;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateBackupCodeHashesAsync(Guid keyId, string[] hashes)
        {
            if (key is not null) key.BackupCodeHashes = hashes;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateLastUsedAsync(Guid keyId) => Task.FromResult(true);
        public Task<UserMfaKey?> GetByIdAsync(Guid id) => Task.FromResult(key);
        public Task<UserMfaKey?> GetByCredentialIdAsync(string credentialIdBase64) => Task.FromResult<UserMfaKey?>(null);
        public Task<UserMfaKey> CreateAsync(UserMfaKey k) => Task.FromResult(k);
        public Task<bool> UpdateSignCountAsync(Guid keyId, uint newSignCount) => Task.FromResult(true);
        public Task<bool> DeactivateAsync(Guid keyId, Guid userId) => Task.FromResult(true);
        public Task<int> DeactivateAllByUserIdAsync(Guid userId) => Task.FromResult(0);
        public Task<int> CountActiveByUserIdAsync(Guid userId) => Task.FromResult(key is null ? 0 : 1);
        public Task<bool> RenameAsync(Guid keyId, Guid userId, string newName) => Task.FromResult(true);
    }

    private sealed class FakeAuthService : IUserAuthService
    {
        public Task<RoleMfaRequirement> GetEffectiveMfaRequirementAsync(Guid userId)
            => Task.FromResult(RoleMfaRequirement.None);

        public Task<TokenPairDto> IssueFullSessionAsync(Guid userId, bool mfaVerified, string? ipAddress, string? userAgent)
            => Task.FromResult(new TokenPairDto { AccessToken = "access", RefreshToken = "refresh", ExpiresInSeconds = 900 });

        public Task<LoginResponseDto> LoginAsync(string loginOrEmail, string password, string? ipAddress, string? userAgent) => throw new NotSupportedException();
        public Task<TokenPairDto> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null) => throw new NotSupportedException();
        public Task LogoutAsync(Guid sessionId) => Task.CompletedTask;
        public Task<StepUpTokenDto> CreateStepUpTokenAsync(Guid userId, string password) => throw new NotSupportedException();
        public Task CompleteFirstAccessAsync(Guid userId, CompleteFirstAccessRequestDto dto) => Task.CompletedTask;
        public Task<FirstAccessStatusDto> GetFirstAccessStatusAsync(Guid userId) => throw new NotSupportedException();
        public Task<(bool CanUse, string? Reason)> CanUseApiTokensAsync(Guid userId) => Task.FromResult((true, (string?)null));
        public Task UnlockAsync(Guid userId) => Task.CompletedTask;
    }

    private sealed class FakeUserRepository(User? user) : IUserRepository
    {
        public Task<User?> GetByIdAsync(Guid id) => Task.FromResult(user);
        public Task<bool> SetMfaFailureStateAsync(Guid userId, int failedAttempts, DateTime? lockoutUntil) => Task.FromResult(true);
        public Task<bool> ResetMfaFailureStateAsync(Guid userId) => Task.FromResult(true);
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> ids) => Task.FromResult<IReadOnlyList<User>>(user is null ? [] : [user]);
        public Task<User?> GetByLoginAsync(string login) => Task.FromResult(user);
        public Task<User?> GetByEmailAsync(string email) => Task.FromResult(user);
        public Task<User?> GetByLoginOrEmailAsync(string loginOrEmail) => Task.FromResult(user);
        public Task<IReadOnlyList<User>> GetAllPageAsync(string? cursor, int take = 50) => Task.FromResult<IReadOnlyList<User>>([]);
        public Task<User> CreateAsync(User u) => Task.FromResult(u);
        public Task<User> UpdateAsync(User u) => Task.FromResult(u);
        public Task<bool> SetMfaConfiguredAsync(Guid userId, bool configured) => Task.FromResult(true);
        public Task<bool> SetLastLoginAsync(Guid userId, DateTime at) => Task.FromResult(true);
        public Task<bool> ExistsByLoginAsync(string login) => Task.FromResult(false);
        public Task<bool> ExistsByEmailAsync(string email) => Task.FromResult(false);
        public Task<int> CountAsync() => Task.FromResult(0);
    }
}
