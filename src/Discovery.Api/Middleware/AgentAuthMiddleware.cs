using Discovery.Api.Services;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Middleware;

/// <summary>
/// Middleware que valida tokens de agent para endpoints protegidos.
/// Espera header: Authorization: Bearer mdz_... (para /api/agent-auth/)
/// Quando habilitado em Security:AgentConnection:EnforceTlsHashValidation,
/// exige também o header X-Agent-Tls-Cert-Hash:
///   - na emissão de credenciais NATS (POST /api/agent-auth/me/nats-credentials)
/// Isso padroniza o gate anti-MITM no canal de credenciais NATS.
/// </summary>
public class AgentAuthMiddleware
{
    private readonly RequestDelegate _next;
    private const string AgentAuthPathPrefix = "/api/v1/agent-auth";
    private const string AgentNatsCredentialsPath = "/api/v1/agent-auth/me/nats-credentials";
    private const string AgentTlsHashHeader = "X-Agent-Tls-Cert-Hash";
    private const string AgentIdHeader = "X-Agent-ID";

    public AgentAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        IAgentAuthService authService,
        IConfiguration configuration,
        IAgentTlsCertificateProbe tlsCertificateProbe)
    {
        var path = context.Request.Path;
        var isAgentApi = path.StartsWithSegments(AgentAuthPathPrefix);

        if (!isAgentApi)
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

        if (isAgentApi)
        {
            var rawAgentIdHeader = context.Request.Headers[AgentIdHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawAgentIdHeader))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Missing X-Agent-ID header." });
                return;
            }

            if (!Guid.TryParse(rawAgentIdHeader, out var headerAgentId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid X-Agent-ID header." });
                return;
            }

            if (headerAgentId != token.AgentId)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "X-Agent-ID does not match authenticated token." });
                return;
            }
        }

        // Gate anti-MITM para emissão de credenciais NATS.
        var requiresTlsHashCheck = path.StartsWithSegments(AgentNatsCredentialsPath);
        if (requiresTlsHashCheck)
        {
            var enforceTlsHashValidation = configuration.GetValue<bool>("Security:AgentConnection:EnforceTlsHashValidation");
            if (enforceTlsHashValidation)
            {
                var tlsHash = context.Request.Headers[AgentTlsHashHeader].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(tlsHash))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Missing X-Agent-Tls-Cert-Hash header." });
                    return;
                }
                var expectedHash = await tlsCertificateProbe.GetExpectedTlsCertHashAsync(context.RequestAborted);
                if (string.IsNullOrWhiteSpace(expectedHash))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "TLS certificate probe unavailable. Retry later." });
                    return;
                }

                if (!string.Equals(expectedHash, tlsHash.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "TLS certificate hash mismatch." });
                    return;
                }

                context.Items["AgentTlsCertHash"] = tlsHash.Trim();
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
