using Discovery.Core.Cqrs.Auth.Queries;
using Discovery.Core.DTOs.Auth;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Cqrs.Auth.QueryHandlers;

namespace Discovery.Tests;

[TestFixture]
public class AuthQueryHandlersTests
{
    [Test]
    public async Task LoginQueryHandler_preserves_ExpiresInSeconds_when_session_is_established_without_mfa()
    {
        var handler = new LoginQueryHandler(new FakeUserAuthService());
        var result = await handler.Handle(
            new LoginQuery("user@empresa.com", "Secret@123456", "127.0.0.1", "Edge"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.SessionEstablished, Is.True);
        Assert.That(result.Value.MfaRequired, Is.False);
        Assert.That(result.Value.AccessToken, Is.EqualTo("access-123"));
        Assert.That(result.Value.RefreshToken, Is.EqualTo("refresh-456"));
        Assert.That(result.Value.ExpiresInSeconds, Is.EqualTo(1800));
    }

    [Test]
    public async Task RefreshTokenQueryHandler_preserves_ExpiresInSeconds()
    {
        var handler = new RefreshTokenQueryHandler(new FakeUserAuthService());
        var result = await handler.Handle(
            new RefreshTokenQuery("refresh-456", "127.0.0.1", "Edge"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.AccessToken, Is.EqualTo("new-access"));
        Assert.That(result.Value.RefreshToken, Is.EqualTo("new-refresh"));
        Assert.That(result.Value.ExpiresInSeconds, Is.EqualTo(1800));
    }

    private sealed class FakeUserAuthService : IUserAuthService
    {
        public Task<LoginResponseDto> LoginAsync(string loginOrEmail, string password, string? ipAddress, string? userAgent)
            => Task.FromResult(new LoginResponseDto
            {
                MfaRequired = false,
                SessionEstablished = true,
                AccessToken = "access-123",
                RefreshToken = "refresh-456",
                ExpiresInSeconds = 1800,
            });

        public Task<TokenPairDto> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null)
            => Task.FromResult(new TokenPairDto
            {
                AccessToken = "new-access",
                RefreshToken = "new-refresh",
                ExpiresInSeconds = 1800,
            });

        public Task LogoutAsync(Guid sessionId) => Task.CompletedTask;
        public Task<StepUpTokenDto> CreateStepUpTokenAsync(Guid userId, string password) => throw new NotImplementedException();
        public Task<TokenPairDto> IssueFullSessionAsync(Guid userId, bool mfaVerified, string? ipAddress, string? userAgent) => throw new NotImplementedException();
        public Task CompleteFirstAccessAsync(Guid userId, CompleteFirstAccessRequestDto dto) => Task.CompletedTask;
        public Task<FirstAccessStatusDto> GetFirstAccessStatusAsync(Guid userId) => throw new NotImplementedException();
        public Task<RoleMfaRequirement> GetEffectiveMfaRequirementAsync(Guid userId) => Task.FromResult(RoleMfaRequirement.None);
        public Task<(bool CanUse, string? Reason)> CanUseApiTokensAsync(Guid userId) => Task.FromResult<(bool, string?)>((true, null));
        public Task UnlockAsync(Guid userId) => Task.CompletedTask;
    }
}
