using Meduza.Core.Interfaces.Auth;
using System.Security.Claims;

namespace Meduza.Api.Middleware;

/// <summary>
/// Middleware que valida tokens JWT de usuário.
/// Extrai UserId e outros claims, armazenando em HttpContext.Items.
/// Não bloqueia requisições — a autorização é feita pelos action filters.
/// Rotas públicas (/api/auth/login, /api/auth/refresh) são liberadas sem JWT.
/// </summary>
public class UserAuthMiddleware
{
    private static readonly HashSet<string> _publicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/login",
        "/api/auth/refresh"
    };

    private readonly RequestDelegate _next;

    public UserAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IJwtService jwtService)
    {
        // Caminhos públicos não precisam de validação
        if (_publicPaths.Contains(context.Request.Path.Value ?? string.Empty))
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
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
                }
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
