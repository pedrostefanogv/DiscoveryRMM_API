using System.Security.Claims;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers ASP.NET Core in-memory output caching for high-frequency read endpoints
/// with strict per-credential, per-host, and per-identity cache isolation.
/// (Microsoft.AspNetCore.OutputCaching.StackExchangeRedis is not referenced;
/// caching uses the default in-memory IOutputCacheStore.)
/// </summary>
public static class OutputCacheServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryOutputCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        _ = configuration;

        services.AddOutputCache(options =>
        {
            options.DefaultExpirationTimeSpan = TimeSpan.FromSeconds(30);
            options.MaximumBodySize = 1024 * 64; // 64 KB

            // BasePolicy: endpoints sem [OutputCache] explícito NÃO devem cachear
            // requisições autenticadas (inclusive X-Api-Key / AgentId / UserId).
            options.AddBasePolicy(builder => builder
                .Expire(TimeSpan.FromSeconds(15))
                .SetVaryByHost(true)
                .SetVaryByHeader("Authorization", "Cookie", "X-Api-Key", "X-Api-Secret")
                .VaryByValue(ctx => new KeyValuePair<string, string>("auth_identity", ResolveAuthIdentityKey(ctx)))
                .AddPolicy<BaseAuthSafetyOutputCachePolicy>());

            // Named policies for explicitly annotated read endpoints (e.g. ClientsController.GetAll).
            options.AddPolicy("Short", builder => ConfigureNamedPolicy(builder, TimeSpan.FromSeconds(10)));
            options.AddPolicy("Medium", builder => ConfigureNamedPolicy(builder, TimeSpan.FromSeconds(30)));
            options.AddPolicy("Long", builder => ConfigureNamedPolicy(builder, TimeSpan.FromMinutes(5)));
        });

        return services;
    }

    private static void ConfigureNamedPolicy(OutputCachePolicyBuilder builder, TimeSpan expiration)
    {
        builder
            .Expire(expiration)
            .SetVaryByHost(true)
            .SetVaryByHeader("Authorization", "Cookie", "X-Api-Key", "X-Api-Secret")
            .VaryByValue(ctx => new KeyValuePair<string, string>("auth_identity", ResolveAuthIdentityKey(ctx)))
            .AddPolicy<NamedAuthSafetyOutputCachePolicy>();
    }

    internal static string ResolveAuthIdentityKey(HttpContext ctx)
    {
        var uid = ctx.Items["UserId"] is Guid u ? u.ToString("N") : string.Empty;
        var api = ctx.Items["IsApiTokenAuth"] is true ? "1" : "0";
        var aid = ctx.Items["AgentId"] is Guid a ? a.ToString("N") : string.Empty;
        var tid = ctx.Items["TokenId"] is Guid t ? t.ToString("N") : string.Empty;
        var cid = ctx.Items["ClientId"] is Guid c ? c.ToString("N") : string.Empty;
        var sid = ctx.Items["SiteId"] is Guid s ? s.ToString("N") : string.Empty;
        var claimsSub = ctx.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? ctx.User?.FindFirst("sub")?.Value
            ?? string.Empty;
        var isUserAuth = ctx.User?.Identity?.IsAuthenticated == true ? "1" : "0";

        if (string.IsNullOrEmpty(uid)
            && api == "0"
            && string.IsNullOrEmpty(aid)
            && string.IsNullOrEmpty(tid)
            && string.IsNullOrEmpty(cid)
            && string.IsNullOrEmpty(sid)
            && string.IsNullOrEmpty(claimsSub)
            && isUserAuth == "0")
        {
            return "anonymous";
        }

        var mfaPending = ctx.Items["MfaPending"] is true ? "1" : "0";
        var mfaSetup = ctx.Items["MfaSetup"] is true ? "1" : "0";
        var stepUp = ctx.Items["StepUp"] is true ? "1" : "0";

        return $"u={uid}|api={api}|a={aid}|t={tid}|c={cid}|s={sid}|sub={claimsSub}|ua={isUserAuth}|f={mfaPending}{mfaSetup}{stepUp}";
    }

    private sealed class BaseAuthSafetyOutputCachePolicy() : AuthSafetyOutputCachePolicy(baseOnly: true);

    private sealed class NamedAuthSafetyOutputCachePolicy() : AuthSafetyOutputCachePolicy(baseOnly: false);

    private abstract class AuthSafetyOutputCachePolicy(bool baseOnly) : IOutputCachePolicy
    {
        public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
        {
            if (baseOnly)
            {
                var ctx = context.HttpContext;
                var req = ctx.Request;
                var hasCredentialsOrIdentity =
                    !StringValues.IsNullOrEmpty(req.Headers["X-Api-Key"])
                    || !StringValues.IsNullOrEmpty(req.Headers["X-Api-Secret"])
                    || !StringValues.IsNullOrEmpty(req.Headers.Authorization)
                    || req.Headers.ContainsKey("Cookie")
                    || ctx.Items.ContainsKey("UserId")
                    || ctx.Items.ContainsKey("AgentId")
                    || ctx.User?.Identity?.IsAuthenticated == true;

                if (hasCredentialsOrIdentity)
                {
                    context.AllowCacheLookup = false;
                    context.AllowCacheStorage = false;
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation)
            => ValueTask.CompletedTask;

        public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
        {
            var ctx = context.HttpContext;
            var response = ctx.Response;

            if (!StringValues.IsNullOrEmpty(response.Headers.SetCookie)
                || response.StatusCode != StatusCodes.Status200OK)
            {
                context.AllowCacheStorage = false;
                return ValueTask.CompletedTask;
            }

            var isAuthenticatedNow = ctx.Items.ContainsKey("UserId")
                || ctx.Items.ContainsKey("AgentId")
                || ctx.User?.Identity?.IsAuthenticated == true;

            if (isAuthenticatedNow)
            {
                if (!context.CacheVaryByRules.VaryByValues.TryGetValue("auth_identity", out var identityKey)
                    || string.IsNullOrWhiteSpace(identityKey)
                    || string.Equals(identityKey, "anonymous", StringComparison.Ordinal))
                {
                    context.AllowCacheStorage = false;
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
