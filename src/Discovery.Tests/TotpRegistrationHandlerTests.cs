using Discovery.Core.Cqrs.Mfa;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Cqrs.Mfa;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OtpNet;
using System.Text;

namespace Discovery.Tests;

/// <summary>
/// O cadastro de TOTP (POST /mfa/totp/register/begin|complete) era documentado e
/// chamado pelo console, mas não existia na API — quem tinha role exigindo TOTP
/// ficava sem conseguir configurar MFA (e sem conseguir logar).
/// </summary>
[TestFixture]
public class TotpRegistrationHandlerTests
{
    private static readonly OtpAuthService Otp = new();

    [Test]
    public async Task Begin_ReturnsSecretAndOtpAuthUriForTheUser()
    {
        var user = NewUser();
        var handler = new BeginTotpRegistrationQueryHandler(
            Otp, new FakeUserRepository(user), new ConfigurationBuilder().Build(),
            NullLogger<BeginTotpRegistrationQueryHandler>.Instance);

        var result = await handler.Handle(new BeginTotpRegistrationQuery(user.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.SecretBase32, Is.Not.Empty);
        Assert.That(result.Value.QrCodeUri, Does.StartWith("otpauth://totp/"));
        Assert.That(result.Value.QrCodeUri, Does.Contain(Uri.EscapeDataString(user.Email)));
    }

    [Test]
    public async Task Complete_WithValidCode_PersistsEncryptedSecretAndBackupCodes()
    {
        var user = NewUser();
        var keys = new FakeMfaKeyRepository();
        var users = new FakeUserRepository(user);
        var handler = new CompleteTotpRegistrationCommandHandler(
            Otp, new FakeSecretProtector(), keys, users,
            NullLogger<CompleteTotpRegistrationCommandHandler>.Instance);

        var (secret, _) = Otp.GenerateSecret("Discovery", user.Email);
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

        var result = await handler.Handle(
            new CompleteTotpRegistrationCommand(user.Id, secret, code, "Authenticator OTP"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.BackupCodes, Has.Count.EqualTo(8));

        Assert.That(keys.Created, Is.Not.Null);
        Assert.That(keys.Created!.KeyType, Is.EqualTo(Discovery.Core.Enums.Security.MfaKeyType.Totp));
        Assert.That(keys.Created.Name, Is.EqualTo("Authenticator OTP"));
        Assert.That(keys.Created.IsActive, Is.True);
        // O segredo nunca é gravado em claro.
        Assert.That(keys.Created.OtpSecretEncrypted, Does.StartWith("enc:"));
        Assert.That(keys.Created.OtpSecretEncrypted, Does.Not.Contain(secret));
        Assert.That(keys.Created.BackupCodeHashes, Has.Length.EqualTo(8));
        Assert.That(users.MfaConfigured, Is.True);
    }

    [Test]
    public async Task Complete_WithInvalidCode_DoesNotPersistAnything()
    {
        var user = NewUser();
        var keys = new FakeMfaKeyRepository();
        var users = new FakeUserRepository(user);
        var handler = new CompleteTotpRegistrationCommandHandler(
            Otp, new FakeSecretProtector(), keys, users,
            NullLogger<CompleteTotpRegistrationCommandHandler>.Instance);

        var (secret, _) = Otp.GenerateSecret("Discovery", user.Email);

        var result = await handler.Handle(
            new CompleteTotpRegistrationCommand(user.Id, secret, "000000", "Authenticator OTP"),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(keys.Created, Is.Null);
        Assert.That(users.MfaConfigured, Is.False);
    }

    [Test]
    public async Task Complete_WithInvalidKeyName_IsRejectedBeforeValidatingTheCode()
    {
        var user = NewUser();
        var keys = new FakeMfaKeyRepository();
        var handler = new CompleteTotpRegistrationCommandHandler(
            Otp, new FakeSecretProtector(), keys, new FakeUserRepository(user),
            NullLogger<CompleteTotpRegistrationCommandHandler>.Instance);

        var (secret, _) = Otp.GenerateSecret("Discovery", user.Email);
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

        var result = await handler.Handle(
            new CompleteTotpRegistrationCommand(user.Id, secret, code, "x"), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(keys.Created, Is.Null);
    }

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

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public bool IsEnabled => true;
        public bool IsProtected(string? value) => value?.StartsWith("enc:") == true;
        // Modela a proteção real: o valor persistido não contém o segredo em claro.
        public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => protectedValue.StartsWith("enc:")
            ? Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[4..]))
            : protectedValue;
        public string UnprotectOrSelf(string? value) => value is null ? string.Empty : Unprotect(value);
    }

    private sealed class FakeMfaKeyRepository : IUserMfaKeyRepository
    {
        public UserMfaKey? Created { get; private set; }

        public Task<UserMfaKey> CreateAsync(UserMfaKey key)
        {
            Created = key;
            return Task.FromResult(key);
        }

        public Task<UserMfaKey?> GetByIdAsync(Guid id) => Task.FromResult<UserMfaKey?>(null);
        public Task<IEnumerable<UserMfaKey>> GetActiveByUserIdAsync(Guid userId) => Task.FromResult<IEnumerable<UserMfaKey>>([]);
        public Task<UserMfaKey?> GetByCredentialIdAsync(string credentialIdBase64) => Task.FromResult<UserMfaKey?>(null);
        public Task<bool> UpdateSignCountAsync(Guid keyId, uint newSignCount) => Task.FromResult(true);
        public Task<bool> UpdateLastUsedAsync(Guid keyId) => Task.FromResult(true);
        public Task<bool> SetLastUsedStepAsync(Guid keyId, long step) => Task.FromResult(true);
        public Task<bool> UpdateBackupCodeHashesAsync(Guid keyId, string[] hashes) => Task.FromResult(true);
        public Task<bool> DeactivateAsync(Guid keyId, Guid userId) => Task.FromResult(true);
        public Task<int> DeactivateAllByUserIdAsync(Guid userId) => Task.FromResult(0);
        public Task<int> CountActiveByUserIdAsync(Guid userId) => Task.FromResult(0);
        public Task<bool> RenameAsync(Guid keyId, Guid userId, string newName) => Task.FromResult(true);
    }

    private sealed class FakeUserRepository(User? user) : IUserRepository
    {
        public bool MfaConfigured { get; private set; }

        public Task<User?> GetByIdAsync(Guid id) => Task.FromResult(user);
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> ids) => Task.FromResult<IReadOnlyList<User>>(user is null ? [] : [user]);
        public Task<User?> GetByLoginAsync(string login) => Task.FromResult(user);
        public Task<User?> GetByEmailAsync(string email) => Task.FromResult(user);
        public Task<User?> GetByLoginOrEmailAsync(string loginOrEmail) => Task.FromResult(user);
        public Task<IReadOnlyList<User>> GetAllPageAsync(string? cursor, int take = 50) => Task.FromResult<IReadOnlyList<User>>(user is null ? [] : [user]);
        public Task<User> CreateAsync(User u) => Task.FromResult(u);
        public Task<User> UpdateAsync(User u) => Task.FromResult(u);

        public Task<bool> SetMfaConfiguredAsync(Guid userId, bool configured)
        {
            MfaConfigured = configured;
            return Task.FromResult(true);
        }

        public Task<bool> SetLastLoginAsync(Guid userId, DateTime at) => Task.FromResult(true);
        public Task<bool> SetMfaFailureStateAsync(Guid userId, int failedAttempts, DateTime? lockoutUntil) => Task.FromResult(true);
        public Task<bool> ResetMfaFailureStateAsync(Guid userId) => Task.FromResult(true);
        public Task<bool> ExistsByLoginAsync(string login) => Task.FromResult(false);
        public Task<bool> ExistsByEmailAsync(string email) => Task.FromResult(false);
        public Task<int> CountAsync() => Task.FromResult(user is null ? 0 : 1);
    }
}
