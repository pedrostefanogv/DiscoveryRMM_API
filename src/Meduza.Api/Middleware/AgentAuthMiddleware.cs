using Meduza.Api.Services;
using Meduza.Core.Interfaces;

namespace Meduza.Api.Middleware;

/// <summary>
/// Middleware que valida tokens de agent para endpoints protegidos.
/// Espera header: Authorization: Bearer mdz_... (para /api/agent-auth/)
/// Ou query param: ?access_token=mdz_... (para /hubs/agent - SignalR WebSocket).
/// Quando habilitado em Security:AgentConnection:EnforceTlsHashValidation,
/// exige também o header X-Agent-Tls-Cert-Hash:
///   - no handshake do hub de agents (SignalR)
///   - na emissão de credenciais NATS (POST /api/agent-auth/me/nats-credentials)
/// Isso padroniza o gate anti-MITM tanto para o canal SignalR quanto para o NATS:
/// um agent interceptado por MITM não consegue nem conectar no hub nem obter credenciais NATS.
/// Para o hub, tokens não-agent (JWT de usuário) passam adiante sem bloqueio.
/// </summary>
public class AgentAuthMiddleware
{
    private readonly RequestDelegate _next;
    private const string AgentAuthPathPrefix = "/api/agent-auth";
    private const string AgentHubPath = "/hubs/agent";
    private const string AgentNatsCredentialsPath = "/api/agent-auth/me/nats-credentials";
    private const string AgentTlsHashHeader = "X-Agent-Tls-Cert-Hash";

    public AgentAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        IAgentAuthService authService,
        IConfiguration configuration,
        IAgentTlsCertificateProbe tlsCertificateProbe)
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

        // Gate anti-MITM unificado: aplica-se tanto ao connect SignalR quanto à emissão de
        // credenciais NATS. Ambos os canais exigem o mesmo hash TLS quando a flag está ativa,
        // impedindo que um MITM obtenha credenciais válidas para qualquer dos dois transports.
        var requiresTlsHashCheck = isAgentHub || path.StartsWithSegments(AgentNatsCredentialsPath);
        if (requiresTlsHashCheck)
        {
            var enforceTlsHashValidation = configuration.GetValue<bool>("Security:AgentConnection:EnforceTlsHashValidation");
            if (enforceTlsHashValidation)
            {
                var tlsHash = context.Request.Headers[AgentTlsHashHeader].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(tlsHash))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    if (!isAgentHub)
                        await context.Response.WriteAsJsonAsync(new { error = "Missing X-Agent-Tls-Cert-Hash header." });
                    return;
                }
                var expectedHash = await tlsCertificateProbe.GetExpectedTlsCertHashAsync(context.RequestAborted);
                if (string.IsNullOrWhiteSpace(expectedHash))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    if (!isAgentHub)
                        await context.Response.WriteAsJsonAsync(new { error = "TLS certificate probe unavailable. Retry later." });
                    return;
                }

                if (!string.Equals(expectedHash, tlsHash.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    if (!isAgentHub)
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
