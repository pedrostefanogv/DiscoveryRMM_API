using Discovery.Core.Interfaces.Auth;
using System.Security.Claims;

namespace Discovery.Api.Middleware;

/// <summary>
/// Middleware que valida tokens JWT de usuário.
/// Extrai UserId e outros claims, armazenando em HttpContext.Items.
/// Não bloqueia requisições — a autorização é feita pelos action filters.
/// Rotas públicas (/api/v1/auth/login, /api/v1/auth/refresh) são liberadas sem JWT.
/// </summary>
public class UserAuthMiddleware
{
    private static readonly HashSet<string> _publicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/auth/login",
        "/api/v1/auth/refresh"
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<UserAuthMiddleware> _logger;

    public UserAuthMiddleware(RequestDelegate next, ILogger<UserAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IJwtService jwtService)
    {
        // Caminhos públicos não precisam de validação
        if (_publicPaths.Contains(context.Request.Path.Value ?? string.Empty))
        {
            await _next(context);
            return;
        }

        var isNatsWebSocketPath = context.Request.Path.Value?.StartsWith("/nats", StringComparison.OrdinalIgnoreCase) == true;

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        // Para NATS WS, o cliente pode enviar JWT em access_token na query string.
        if (string.IsNullOrEmpty(authHeader) && isNatsWebSocketPath)
        {
            var queryToken = context.Request.Query["access_token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(queryToken))
            {
                if (queryToken.StartsWith("mdz_", StringComparison.OrdinalIgnoreCase))
                {
                    // Token de agent — o AgentAuthMiddleware já processou, não tocamos aqui.
                    _logger.LogDebug(
                        "UserAuthMiddleware: token de agent na query string do hub, ignorando. Path={Path}",
                        context.Request.Path.Value);
                }
                else
                {
                    authHeader = $"Bearer {queryToken}";
                }
            }
        }

        if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            !authHeader.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            var principal = jwtService.ValidateToken(token);
            if (principal is not null)
            {
                var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal.FindFirstValue("sub");
                if (Guid.TryParse(userIdClaim, out var userId))
                {
                    context.Items["UserId"] = userId;
                    context.Items["MfaPending"] = principal.FindFirstValue("mfa_pending") == "true";
                    context.Items["MfaSetup"] = principal.FindFirstValue("mfa_setup") == "true";

                    var jti = principal.FindFirstValue("jti");
                    if (!string.IsNullOrEmpty(jti))
                        context.Items["SessionId"] = jti;

                    _logger.LogDebug(
                        "UserAuthMiddleware: JWT validado. UserId={UserId}, " +
                        "Path={Path}, MfaPending={MfaPending}, MfaSetup={MfaSetup}",
                        userId, context.Request.Path.Value,
                        context.Items["MfaPending"], context.Items["MfaSetup"]);
                }
                else
                {
                    _logger.LogWarning(
                        "UserAuthMiddleware: JWT validado mas UserId invalido. " +
                        "sub={Sub}, Path={Path}",
                        userIdClaim, context.Request.Path.Value);
                }
            }
            else if (isNatsWebSocketPath)
            {
                _logger.LogWarning(
                    "UserAuthMiddleware: JWT invalido/expirado em conexao NATS WS. " +
                    "Path={Path}, TokenPrefix={TokenPrefix}...",
                    context.Request.Path.Value,
                    token.Length > 20 ? token[..20] : token);
            }
        }

        await _next(context);
    }
}

public static class UserAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseUserAuth(this IApplicationBuilder app)
        => app.UseMiddleware<UserAuthMiddleware>();
}
