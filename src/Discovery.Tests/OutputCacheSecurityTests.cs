using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using Discovery.Api.DependencyInjection;
using Discovery.Api.Middleware;
using Discovery.Core.DTOs.ApiTokens;
using Discovery.Core.DTOs.Auth;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class OutputCacheSecurityTests
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Test]
    public async Task OutputCache_XApiKey_IsolatesCacheAcrossTenantsAndUsers()
    {
        var callCount = 0;
        var (pipeline, sp) = BuildPipeline(
            withMediumPolicyEndpoint: true,
            handler: async ctx =>
            {
                callCount++;
                var uid = ctx.Items["UserId"]?.ToString() ?? "anon";
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                await ctx.Response.WriteAsync($"user:{uid}:calls={callCount}");
            });

        await using (sp)
        {
            var r1 = await SendGetAsync(pipeline, sp, "/api/v1/clients", ("X-Api-Key", "keyA"), ("X-Api-Secret", "secA"));
            Assert.That(r1.StatusCode, Is.EqualTo(200));
            Assert.That(r1.Body, Is.EqualTo($"user:{UserA}:calls=1"));

            // Same API key hits cache
            var r2 = await SendGetAsync(pipeline, sp, "/api/v1/clients", ("X-Api-Key", "keyA"), ("X-Api-Secret", "secA"));
            Assert.That(r2.StatusCode, Is.EqualTo(200));
            Assert.That(r2.Body, Is.EqualTo($"user:{UserA}:calls=1"));

            // Different API key / user must MISS cache and never receive UserA's cached payload
            var r3 = await SendGetAsync(pipeline, sp, "/api/v1/clients", ("X-Api-Key", "keyB"), ("X-Api-Secret", "secB"));
            Assert.That(r3.StatusCode, Is.EqualTo(200));
            Assert.That(r3.Body, Is.EqualTo($"user:{UserB}:calls=2"));

            // Invalid secret must return 401 BEFORE OutputCache runs
            var r4 = await SendGetAsync(pipeline, sp, "/api/v1/clients", ("X-Api-Key", "keyA"), ("X-Api-Secret", "wrong"));
            Assert.That(r4.StatusCode, Is.EqualTo(401));
            Assert.That(callCount, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task OutputCache_DoesNotLeakAuthenticatedResponseToUnauthenticatedCaller()
    {
        var callCount = 0;
        var (pipeline, sp) = BuildPipeline(
            withMediumPolicyEndpoint: true,
            handler: async ctx =>
            {
                callCount++;
                var uid = ctx.Items["UserId"]?.ToString() ?? "anon";
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                await ctx.Response.WriteAsync($"user:{uid}:calls={callCount}");
            });

        await using (sp)
        {
            var authed = await SendGetAsync(pipeline, sp, "/api/v1/clients", ("X-Api-Key", "keyA"), ("X-Api-Secret", "secA"));
            Assert.That(authed.Body, Is.EqualTo($"user:{UserA}:calls=1"));

            var anon = await SendGetAsync(pipeline, sp, "/api/v1/clients");
            Assert.That(anon.Body, Is.EqualTo("user:anon:calls=2"));
            Assert.That(anon.Body, Does.Not.Contain(UserA.ToString()));
        }
    }

    [Test]
    public async Task OutputCache_AuthorizationBearer_NotServedCrossUserAndRespectsDefaultPolicy()
    {
        var callCount = 0;
        var (pipeline, sp) = BuildPipeline(
            withMediumPolicyEndpoint: true,
            handler: async ctx =>
            {
                callCount++;
                var uid = ctx.Items["UserId"]?.ToString() ?? "anon";
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                await ctx.Response.WriteAsync($"bearer:{uid}:calls={callCount}");
            });

        await using (sp)
        {
            var r1 = await SendGetAsync(pipeline, sp, "/api/v1/clients", ("Authorization", "Bearer jwtA"));
            var r2 = await SendGetAsync(pipeline, sp, "/api/v1/clients", ("Authorization", "Bearer jwtA"));
            var r3 = await SendGetAsync(pipeline, sp, "/api/v1/clients", ("Authorization", "Bearer jwtB"));

            Assert.That(r1.Body, Is.EqualTo($"bearer:{UserA}:calls=1"));
            Assert.That(r2.Body, Is.EqualTo($"bearer:{UserA}:calls=2"));
            Assert.That(r3.Body, Is.EqualTo($"bearer:{UserB}:calls=3"));
        }
    }

    [Test]
    public async Task OutputCache_PipelineRunsSecurityHeadersBeforeCacheHit()
    {
        var callCount = 0;
        var (pipeline, sp) = BuildPipeline(
            withMediumPolicyEndpoint: true,
            handler: async ctx =>
            {
                callCount++;
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                await ctx.Response.WriteAsync($"ok:{callCount}");
            });

        await using (sp)
        {
            var miss = await SendGetAsync(pipeline, sp, "/api/v1/clients", ("X-Api-Key", "keyA"), ("X-Api-Secret", "secA"));
            Assert.That(miss.Body, Is.EqualTo("ok:1"));
            Assert.That(miss.Headers["X-Content-Type-Options"].ToString(), Is.EqualTo("nosniff"));
            Assert.That(miss.Headers["X-Frame-Options"].ToString(), Is.EqualTo("DENY"));

            var hit = await SendGetAsync(pipeline, sp, "/api/v1/clients", ("X-Api-Key", "keyA"), ("X-Api-Secret", "secA"));
            Assert.That(hit.Body, Is.EqualTo("ok:1"));
            Assert.That(hit.Headers["X-Content-Type-Options"].ToString(), Is.EqualTo("nosniff"));
            Assert.That(hit.Headers["X-Frame-Options"].ToString(), Is.EqualTo("DENY"));
        }
    }

    [Test]
    public async Task OutputCache_BasePolicy_DisablesCachingForAuthenticatedApiKeyRequests()
    {
        var callCount = 0;
        var (pipeline, sp) = BuildPipeline(
            withMediumPolicyEndpoint: false,
            handler: async ctx =>
            {
                callCount++;
                var uid = ctx.Items["UserId"]?.ToString() ?? "anon";
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                await ctx.Response.WriteAsync($"base:{uid}:calls={callCount}");
            });

        await using (sp)
        {
            var r1 = await SendGetAsync(pipeline, sp, "/api/v1/tickets", ("X-Api-Key", "keyA"), ("X-Api-Secret", "secA"));
            var r2 = await SendGetAsync(pipeline, sp, "/api/v1/tickets", ("X-Api-Key", "keyA"), ("X-Api-Secret", "secA"));
            Assert.That(r1.Body, Is.EqualTo($"base:{UserA}:calls=1"));
            Assert.That(r2.Body, Is.EqualTo($"base:{UserA}:calls=2"));

            // Anonymous GET under BasePolicy caches as expected
            var anon1 = await SendGetAsync(pipeline, sp, "/api/v1/public-info");
            var anon2 = await SendGetAsync(pipeline, sp, "/api/v1/public-info");
            Assert.That(anon1.Body, Is.EqualTo("base:anon:calls=3"));
            Assert.That(anon2.Body, Is.EqualTo("base:anon:calls=3"));
        }
    }

    [Test]
    public void ProgramCs_MiddlewareOrder_PlacesUseOutputCacheAfterCorsSecurityAndAuth()
    {
        var programPath = Path.Combine(RepoRoot(), "src", "Discovery.Api", "Program.cs");
        var source = File.ReadAllText(programPath);

        var idxRateLimiter = source.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal);
        var idxCors = source.IndexOf("app.UseCors(\"DefaultApi\");", StringComparison.Ordinal);
        var idxSecHeaders = source.IndexOf("app.UseSecurityHeaders();", StringComparison.Ordinal);
        var idxAgentAuth = source.IndexOf("app.UseAgentAuth();", StringComparison.Ordinal);
        var idxApiTokenAuth = source.IndexOf("app.UseApiTokenAuth();", StringComparison.Ordinal);
        var idxUserAuth = source.IndexOf("app.UseUserAuth();", StringComparison.Ordinal);
        var idxOutputCache = source.IndexOf("app.UseOutputCache();", StringComparison.Ordinal);
        var idxMapControllers = source.IndexOf("app.MapControllers();", StringComparison.Ordinal);

        Assert.That(idxRateLimiter, Is.GreaterThan(0));
        Assert.That(idxCors, Is.GreaterThan(idxRateLimiter));
        Assert.That(idxSecHeaders, Is.GreaterThan(idxCors));
        Assert.That(idxAgentAuth, Is.GreaterThan(idxSecHeaders));
        Assert.That(idxApiTokenAuth, Is.GreaterThan(idxAgentAuth));
        Assert.That(idxUserAuth, Is.GreaterThan(idxApiTokenAuth));
        Assert.That(idxOutputCache, Is.GreaterThan(idxUserAuth),
            "UseOutputCache() MUST run after CORS, SecurityHeaders, and all Auth middlewares.");
        Assert.That(idxMapControllers, Is.GreaterThan(idxOutputCache));
    }

    private static string RepoRoot([CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    private static (RequestDelegate Pipeline, ServiceProvider Services) BuildPipeline(
        bool withMediumPolicyEndpoint,
        RequestDelegate handler)
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddRouting();
        services.AddDiscoveryOutputCache(config);
        services.AddSingleton<IApiTokenService, StubApiTokenService>();
        services.AddSingleton<IUserAuthService, StubUserAuthService>();
        services.AddSingleton<IUserRepository, StubUserRepository>();
        services.AddSingleton<IJwtService, StubJwtService>();
        services.AddSingleton<IUserSessionRepository, StubUserSessionRepository>();

        var sp = services.BuildServiceProvider();
        var app = new ApplicationBuilder(sp);

        if (withMediumPolicyEndpoint)
        {
            var endpoint = new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(new OutputCacheAttribute { PolicyName = "Medium" }),
                "MediumPolicyEndpoint");

            app.Use((ctx, next) =>
            {
                ctx.SetEndpoint(endpoint);
                return next();
            });
        }

        app.UseSecurityHeaders();
        app.UseApiTokenAuth();
        app.UseUserAuth();
        app.UseOutputCache();
        app.Run(handler);

        return (app.Build(), sp);
    }

    private static async Task<(int StatusCode, string Body, IHeaderDictionary Headers)> SendGetAsync(
        RequestDelegate pipeline,
        IServiceProvider sp,
        string path,
        params (string Key, string Value)[] headers)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = sp
        };

        var responseFeature = new TestHttpResponseFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseFeature.Body));

        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = path;

        foreach (var (key, value) in headers)
            context.Request.Headers[key] = value;

        await pipeline(context);
        await responseFeature.FireOnStartingIfNeededAsync();

        responseFeature.Buffer.Position = 0;
        using var reader = new StreamReader(responseFeature.Buffer, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, body, context.Response.Headers);
    }

    private sealed class TestHttpResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];
        private bool _started;

        public TestHttpResponseFeature()
        {
            Body = new TriggerStream(this, Buffer);
        }

        public MemoryStream Buffer { get; } = new();
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; }
        public bool HasStarted => _started;

        public void OnStarting(Func<object, Task> callback, object state)
            => _onStarting.Add((callback, state));

        public void OnCompleted(Func<object, Task> callback, object state) { }

        public async Task FireOnStartingIfNeededAsync()
        {
            if (_started)
                return;
            _started = true;
            for (var i = _onStarting.Count - 1; i >= 0; i--)
            {
                var (cb, state) = _onStarting[i];
                await cb(state);
            }
        }

        private sealed class TriggerStream(TestHttpResponseFeature owner, MemoryStream inner) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
            {
                owner.FireOnStartingIfNeededAsync().GetAwaiter().GetResult();
                inner.Write(buffer, offset, count);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await owner.FireOnStartingIfNeededAsync();
                await inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await owner.FireOnStartingIfNeededAsync();
                await inner.WriteAsync(buffer, cancellationToken);
            }
        }
    }

    private sealed class StubApiTokenService : IApiTokenService
    {
        public Task<Guid?> AuthenticateAsync(string rawKey) => rawKey switch
        {
            "keyA.secA" => Task.FromResult<Guid?>(UserA),
            "keyB.secB" => Task.FromResult<Guid?>(UserB),
            _ => Task.FromResult<Guid?>(null)
        };

        public Task<CreateApiTokenResponseDto> CreateTokenAsync(Guid userId, string name, DateTime? expiresAt) => throw new NotSupportedException();
        public Task<bool> RevokeAsync(Guid tokenId, Guid requestingUserId) => throw new NotSupportedException();
        public Task<IEnumerable<ApiTokenSummaryDto>> GetByUserAsync(Guid userId) => throw new NotSupportedException();
    }

    private sealed class StubUserAuthService : IUserAuthService
    {
        public Task<(bool CanUse, string? Reason)> CanUseApiTokensAsync(Guid userId)
            => Task.FromResult<(bool, string?)>((true, null));

        public Task<LoginResponseDto> LoginAsync(string loginOrEmail, string password, string? ipAddress, string? userAgent) => throw new NotSupportedException();
        public Task<TokenPairDto> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null) => throw new NotSupportedException();
        public Task LogoutAsync(Guid sessionId) => throw new NotSupportedException();
        public Task<StepUpTokenDto> CreateStepUpTokenAsync(Guid userId, string password) => throw new NotSupportedException();
        public Task<TokenPairDto> IssueFullSessionAsync(Guid userId, bool mfaVerified, string? ipAddress, string? userAgent) => throw new NotSupportedException();
        public Task CompleteFirstAccessAsync(Guid userId, CompleteFirstAccessRequestDto dto) => throw new NotSupportedException();
        public Task<FirstAccessStatusDto> GetFirstAccessStatusAsync(Guid userId) => throw new NotSupportedException();
        public Task<RoleMfaRequirement> GetEffectiveMfaRequirementAsync(Guid userId) => throw new NotSupportedException();
        public Task UnlockAsync(Guid userId) => throw new NotSupportedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public Task<User?> GetByIdAsync(Guid id)
            => Task.FromResult<User?>(new User { Id = id, Login = $"user-{id:N}" });

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> ids) => throw new NotSupportedException();
        public Task<User?> GetByLoginAsync(string login) => throw new NotSupportedException();
        public Task<User?> GetByEmailAsync(string email) => throw new NotSupportedException();
        public Task<User?> GetByLoginOrEmailAsync(string loginOrEmail) => throw new NotSupportedException();
        public Task<IReadOnlyList<User>> GetAllPageAsync(string? cursor, int take = 50) => throw new NotSupportedException();
        public Task<User> CreateAsync(User user) => throw new NotSupportedException();
        public Task<User> UpdateAsync(User user) => throw new NotSupportedException();
        public Task<bool> SetMfaConfiguredAsync(Guid userId, bool configured) => throw new NotSupportedException();
        public Task<bool> SetLastLoginAsync(Guid userId, DateTime at) => throw new NotSupportedException();
        public Task<bool> SetMfaFailureStateAsync(Guid userId, int failedAttempts, DateTime? lockoutUntil) => throw new NotSupportedException();
        public Task<bool> ResetMfaFailureStateAsync(Guid userId) => throw new NotSupportedException();
        public Task<bool> ExistsByLoginAsync(string login) => throw new NotSupportedException();
        public Task<bool> ExistsByEmailAsync(string email) => throw new NotSupportedException();
        public Task<int> CountAsync() => throw new NotSupportedException();
    }

    private sealed class StubJwtService : IJwtService
    {
        public ClaimsPrincipal? ValidateToken(string token)
        {
            var userId = token switch
            {
                "jwtA" => UserA,
                "jwtB" => UserB,
                _ => Guid.Empty
            };
            if (userId == Guid.Empty)
                return null;

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim("unique_name", $"user-{userId:N}")
            ], "Bearer");

            return new ClaimsPrincipal(identity);
        }

        public string GenerateAccessToken(Guid userId, Guid sessionId, IEnumerable<Claim>? extraClaims = null) => throw new NotSupportedException();
        public string GenerateMfaPendingToken(Guid userId) => throw new NotSupportedException();
        public string GenerateMfaSetupToken(Guid userId) => throw new NotSupportedException();
        public string GenerateStepUpToken(Guid userId) => throw new NotSupportedException();
        public (byte[] tokenBytes, string tokenBase64, string tokenHash) GenerateRefreshToken() => throw new NotSupportedException();
    }

    private sealed class StubUserSessionRepository : IUserSessionRepository
    {
        public Task<UserSession?> GetByIdAsync(Guid id) => Task.FromResult<UserSession?>(null);
        public Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash) => throw new NotSupportedException();
        public Task<UserSession?> GetByRefreshTokenHashWithGracePeriodAsync(string refreshTokenHash) => throw new NotSupportedException();
        public Task<IEnumerable<UserSession>> GetActiveByUserIdAsync(Guid userId) => throw new NotSupportedException();
        public Task<UserSession> CreateAsync(UserSession session) => throw new NotSupportedException();
        public Task<bool> RevokeAsync(Guid sessionId) => throw new NotSupportedException();
        public Task<bool> RevokeWithGracePeriodAsync(Guid sessionId, TimeSpan gracePeriod) => throw new NotSupportedException();
        public Task<bool> RevokeAllByUserIdAsync(Guid userId) => throw new NotSupportedException();
        public Task<bool> UpdateAccessTokenHashAsync(Guid sessionId, string newHash, DateTime newExpiry) => throw new NotSupportedException();
    }
}
