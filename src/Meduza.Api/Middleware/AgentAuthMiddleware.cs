using Meduza.Core.Interfaces;

namespace Meduza.Api.Middleware;

/// <summary>
/// Middleware que valida tokens de agent para endpoints protegidos.
/// Espera header: Authorization: Bearer mdz_...
/// Aplica-se apenas a rotas que começam com /api/agent-auth/.
/// </summary>
public class AgentAuthMiddleware
{
    private readonly RequestDelegate _next;
    private const string AgentAuthPathPrefix = "/api/agent-auth";

    public AgentAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IAgentAuthService authService)
    {
        if (!context.Request.Path.StartsWithSegments(AgentAuthPathPrefix))
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid Authorization header." });
            return;
        }

        var rawToken = authHeader["Bearer ".Length..].Trim();
        var token = await authService.ValidateTokenAsync(rawToken);
        if (token is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired token." });
            return;
        }

        // Disponibilizar info do agent no HttpContext
        context.Items["AgentId"] = token.AgentId;
        context.Items["TokenId"] = token.Id;

        await _next(context);
    }
}

public static class AgentAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseAgentAuth(this IApplicationBuilder app)
        => app.UseMiddleware<AgentAuthMiddleware>();
}
