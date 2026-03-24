using Meduza.Core.Interfaces;

namespace Meduza.Api.Middleware;

/// <summary>
/// Middleware que valida tokens de agent para endpoints protegidos.
/// Espera header: Authorization: Bearer mdz_... (para /api/agent-auth/)
/// Ou query param: ?access_token=mdz_... (para /hubs/agent - SignalR WebSocket).
/// Para o hub, tokens não-agent (JWT de usuário) passam adiante sem bloqueio.
/// </summary>
public class AgentAuthMiddleware
{
    private readonly RequestDelegate _next;
    private const string AgentAuthPathPrefix = "/api/agent-auth";
    private const string AgentHubPath = "/hubs/agent";

    public AgentAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IAgentAuthService authService)
    {
        var path = context.Request.Path;
        var isAgentApi = path.StartsWithSegments(AgentAuthPathPrefix);
        var isAgentHub = path.StartsWithSegments(AgentHubPath);

        if (!isAgentApi && !isAgentHub)
        {
            await _next(context);
            return;
        }

        string? rawToken;

        if (isAgentHub)
        {
            // SignalR WebSocket: token via query param (browsers não podem enviar headers customizados no WS)
            rawToken = context.Request.Query["access_token"].FirstOrDefault();
            if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith("mdz_", StringComparison.OrdinalIgnoreCase))
            {
                // Não é um agent — pode ser um usuário do dashboard, deixa passar
                await _next(context);
                return;
            }
        }
        else
        {
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid Authorization header." });
                return;
            }
            rawToken = authHeader["Bearer ".Length..].Trim();
        }

        var token = await authService.ValidateTokenAsync(rawToken);
        if (token is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            if (!isAgentHub)
                await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired token." });
            return;
        }

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
