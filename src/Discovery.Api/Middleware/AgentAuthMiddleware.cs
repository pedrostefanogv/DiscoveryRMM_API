using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Discovery.Api.Middleware;

/// <summary>
/// Middleware que valida tokens de agent para endpoints protegidos.
/// Espera header: Authorization: Bearer mdz_... (para /api/agent-auth/)
///
/// O prefixo pode ser configurado via appsettings.json:
///   "AgentAuth": { "PathPrefix": "/api/v2/agent-auth" }
/// O default é "/api/v1/agent-auth".
/// </summary>
public class AgentAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _agentAuthPathPrefix;
    private readonly ILogger<AgentAuthMiddleware> _logger;
    private const string AgentIdHeader = "X-Agent-ID";
    private const string DefaultPathPrefix = "/api/v1/agent-auth";

    public AgentAuthMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<AgentAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _agentAuthPathPrefix = configuration.GetValue<string>("AgentAuth:PathPrefix") ?? DefaultPathPrefix;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IAgentAuthService authService)
    {
        var path = context.Request.Path;
        var isAgentApi = path.StartsWithSegments(_agentAuthPathPrefix);

        if (!isAgentApi)
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[AgentAuth] 401: Missing/invalid Authorization header for {Path}", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid Authorization header." });
            return;
        }
        var rawToken = authHeader["Bearer ".Length..].Trim();

        var token = await authService.ValidateTokenAsync(rawToken);
        if (token is null)
        {
            _logger.LogWarning("[AgentAuth] 401: Invalid/expired token for {Path} (token prefix: {Prefix})",
                path, rawToken.Length > 12 ? rawToken[..12] + "..." : "too short");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired token." });
            return;
        }

        if (isAgentApi)
        {
            var rawAgentIdHeader = context.Request.Headers[AgentIdHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawAgentIdHeader))
            {
                // ── Fallback: endpoints de registro de tools/status podem usar o AgentId do token ──
                if (path.Value?.Contains("agent-tools/registry", StringComparison.OrdinalIgnoreCase) == true
                    || path.Value?.Contains("agent-tools", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogInformation("[AgentAuth] Registry endpoint sem X-Agent-ID, usando AgentId do token: {AgentId}",
                        token.AgentId);
                    context.Items["AgentId"] = token.AgentId;
                    context.Items["TokenId"] = token.Id;
                    await _next(context);
                    return;
                }

                _logger.LogWarning("[AgentAuth] 401: Missing X-Agent-ID header for {Path} (token AgentId={AgentId})",
                    path, token.AgentId);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Missing X-Agent-ID header." });
                return;
            }

            if (!Guid.TryParse(rawAgentIdHeader, out var headerAgentId))
            {
                _logger.LogWarning("[AgentAuth] 400: Invalid X-Agent-ID format for {Path}: '{Value}'",
                    path, rawAgentIdHeader);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid X-Agent-ID header." });
                return;
            }

            if (headerAgentId != token.AgentId)
            {
                _logger.LogWarning("[AgentAuth] 401: X-Agent-ID mismatch for {Path}: header={HeaderId}, token={TokenId}",
                    path, headerAgentId, token.AgentId);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "X-Agent-ID does not match authenticated token." });
                return;
            }
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
