using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Discovery.Api.Middleware;
using Discovery.Core.Entities.Security;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// C6/grace: o middleware deve aceitar o access token de uma sessão que foi
/// rotacionada pelo PRÓPRIO cliente (revogada com grace period) e rejeitar
/// sessões revogadas sem grace (logout) e tokens obsoletos (hash divergente).
/// </summary>
public class UserAuthMiddlewareTests
{
    private const string SessionId = "01a17a33-4236-70ce-98a7-c808ff077782";
    private const string UserId = "d66b74de-95d7-41ae-bf85-581b781c51b4";

    private static string ComputeHash(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static DefaultHttpContext CreateContext(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/remote-sessions/00000000-0000-0000-0000-000000000001";
        context.Request.Headers.Append("Authorization", $"Bearer {token}");
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Test]
    public async Task InvokeAsync_ShouldAcceptToken_WhenSessionRevokedWithinRotationGrace()
    {
        var token = "stale-access-token-jwt";
        var session = new UserSession
        {
            Id = Guid.Parse(SessionId),
            UserId = Guid.Parse(UserId),
            AccessTokenHash = ComputeHash(token),
            RefreshTokenHash = "refresh-hash",
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            RevokedAt = DateTime.UtcNow.AddSeconds(-1),
            RefreshTokenGracePeriodUntil = DateTime.UtcNow.AddMinutes(5),
        };

        var context = CreateContext(token);
        var nextCalled = false;
        var middleware = new UserAuthMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<UserAuthMiddleware>.Instance);
        await middleware.InvokeAsync(context, new FakeJwtService(Guid.Parse(UserId), Guid.Parse(SessionId)), new FakeSessionRepository(session));

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(nextCalled, Is.True);
    }

    [Test]
    public async Task InvokeAsync_ShouldReturn401_WhenSessionRevokedWithoutGrace()
    {
        var token = "stale-access-token-jwt";
        var session = new UserSession
        {
            Id = Guid.Parse(SessionId),
            UserId = Guid.Parse(UserId),
            AccessTokenHash = ComputeHash(token),
            RefreshTokenHash = "refresh-hash",
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            RevokedAt = DateTime.UtcNow.AddSeconds(-1),
        };

        var context = CreateContext(token);
        var middleware = new UserAuthMiddleware(_ => Task.CompletedTask, NullLogger<UserAuthMiddleware>.Instance);
        await middleware.InvokeAsync(context, new FakeJwtService(Guid.Parse(UserId), Guid.Parse(SessionId)), new FakeSessionRepository(session));

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task InvokeAsync_ShouldReturn401_WhenAccessTokenHashMismatchedEvenWithinGrace()
    {
        var token = "stale-access-token-jwt";
        var session = new UserSession
        {
            Id = Guid.Parse(SessionId),
            UserId = Guid.Parse(UserId),
            AccessTokenHash = ComputeHash("a-different-token"),
            RefreshTokenHash = "refresh-hash",
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            RevokedAt = DateTime.UtcNow.AddSeconds(-1),
            RefreshTokenGracePeriodUntil = DateTime.UtcNow.AddMinutes(5),
        };

        var context = CreateContext(token);
        var middleware = new UserAuthMiddleware(_ => Task.CompletedTask, NullLogger<UserAuthMiddleware>.Instance);
        await middleware.InvokeAsync(context, new FakeJwtService(Guid.Parse(UserId), Guid.Parse(SessionId)), new FakeSessionRepository(session));

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    private sealed class FakeJwtService : IJwtService
    {
        private readonly Guid _userId;
        private readonly Guid _sessionId;

        public FakeJwtService(Guid userId, Guid sessionId)
        {
            _userId = userId;
            _sessionId = sessionId;
        }

        public string GenerateAccessToken(Guid userId, Guid sessionId, IEnumerable<Claim>? extraClaims = null) => "jwt";

        public string GenerateMfaPendingToken(Guid userId) => "jwt";

        public string GenerateMfaSetupToken(Guid userId) => "jwt";

        public string GenerateStepUpToken(Guid userId) => "jwt";

        public (byte[] tokenBytes, string tokenBase64, string tokenHash) GenerateRefreshToken()
            => (new byte[32], Convert.ToBase64String(new byte[32]), "hash");

        public ClaimsPrincipal? ValidateToken(string token)
        {
            if (token != "stale-access-token-jwt") return null;
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
                new Claim("jti", _sessionId.ToString()),
                new Claim("mfa_verified", "true"),
            ], "test");
            return new ClaimsPrincipal(identity);
        }
    }

    private sealed class FakeSessionRepository(UserSession? session) : IUserSessionRepository
    {
        public Task<UserSession?> GetByIdAsync(Guid id)
            => Task.FromResult(session is not null && session.Id == id ? session : null);

        public Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash) => Task.FromResult<UserSession?>(null);

        public Task<UserSession?> GetByRefreshTokenHashWithGracePeriodAsync(string refreshTokenHash) => Task.FromResult<UserSession?>(null);

        public Task<IEnumerable<UserSession>> GetActiveByUserIdAsync(Guid userId)
            => Task.FromResult<IEnumerable<UserSession>>([]);

        public Task<UserSession> CreateAsync(UserSession newSession) => Task.FromResult(newSession);

        public Task<bool> RevokeAsync(Guid sessionId) => Task.FromResult(false);

        public Task<bool> RevokeWithGracePeriodAsync(Guid sessionId, TimeSpan gracePeriod) => Task.FromResult(false);

        public Task<bool> RevokeAllByUserIdAsync(Guid userId) => Task.FromResult(false);

        public Task<bool> UpdateAccessTokenHashAsync(Guid sessionId, string newHash, DateTime newExpiry) => Task.FromResult(false);
    }
}
