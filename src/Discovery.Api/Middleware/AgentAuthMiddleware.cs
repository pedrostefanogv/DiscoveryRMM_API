using System.Security.Cryptography.X509Certificates;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
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
    private readonly bool _strictMtls;
    private readonly ILogger<AgentAuthMiddleware> _logger;
    private const string AgentIdHeader = "X-Agent-ID";
    private const string DefaultPathPrefix = "/api/v1/agent-auth";

    public AgentAuthMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<AgentAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _agentAuthPathPrefix = configuration.GetValue<string>("AgentAuth:PathPrefix") ?? DefaultPathPrefix;
        // mTLS é ENTRE AGENTES (handshake libp2p + gates de artefato no P2P), não
        // no HTTP. O TLS dos agentes termina na borda do Cloudflare (proxy laranja),
        // que não envia CertificateRequest nem repassa o certificado cliente à
        // origem: exigir mTLS aqui deixava TODOS os agentes em 401
        // MTLS_CERT_REQUIRED (incidente 27/11/2026) sem que o certificado pudesse
        // chegar ao servidor. Por isso o gate no HTTP é OPT-IN (default false).
        _strictMtls = configuration.GetValue<bool?>("AgentMtls:StrictMode") ?? false;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IAgentAuthService authService,
        IAgentRepository agentLookup)
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

        var validation = await authService.ValidateTokenWithStatusAsync(rawToken);
        if (validation is null)
        {
            _logger.LogWarning("[AgentAuth] 401: Invalid/expired token for {Path} (token prefix: {Prefix})",
                path, rawToken.Length > 12 ? rawToken[..12] + "..." : "too short");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired token." });
            return;
        }
        var token = validation.Token;

        if (isAgentApi)
        {
            var rawAgentIdHeader = context.Request.Headers[AgentIdHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rawAgentIdHeader))
            {
                // ── Fallback: endpoints de AUTO-IDENTIDADE podem usar o AgentId do token ──
                // O agent recuperado apenas com token (sem X-Agent-ID) precisa ler a
                // própria identidade/configuração ANTES de conhecer o próprio AgentId.
                // O token já foi validado acima; aqui só resolvemos o id pelo próprio
                // token (nunca logamos o token).
                if (IsSelfIdentityEndpoint(path))
                {
                    _logger.LogInformation(
                        "[AgentAuth] Self-identity endpoint sem X-Agent-ID, usando AgentId do token: {AgentId} ({Path})",
                        token.AgentId, path);
                }
                else if (path.Value?.Contains("agent-tools/registry", StringComparison.OrdinalIgnoreCase) == true
                    || path.Value?.Contains("agent-tools", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Fallback: endpoints de registro de tools/status podem usar o AgentId do token
                    _logger.LogInformation("[AgentAuth] Registry endpoint sem X-Agent-ID, usando AgentId do token: {AgentId}",
                        token.AgentId);
                }
                else
                {
                    _logger.LogWarning("[AgentAuth] 401: Missing X-Agent-ID header for {Path} (token AgentId={AgentId})",
                        path, token.AgentId);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Missing X-Agent-ID header." });
                    return;
                }
            }
            else
            {
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
        }

        // Agente na LIXEIRA: o token continua válido de propósito (é a credencial
        // que permite ao servidor mandar o comando de desinstalação e receber o
        // heartbeat até o uninstall confirmar — ver DeleteAgentCommandHandler).
        // Isso NÃO pode virar acesso livre: enquanto está na lixeira o agente só
        // pode concluir o próprio descomissionamento, que usa exatamente um
        // endpoint (DELETE /agent-auth/me). Todo o resto (inventário, chamados,
        // sensores, automação...) é negado.
        var isTrashed = validation.IsAgentTrashed ?? await IsTrashedAgentAsync(token.AgentId, agentLookup);
        if (isTrashed)
        {
            if (!IsDecommissionEndpoint(context.Request))
            {
                _logger.LogWarning(
                    "[AgentAuth] 403: agente {AgentId} está na lixeira e só pode acessar o próprio descomissionamento (pedido: {Path}).",
                    token.AgentId, path);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Agent is decommissioned; only the decommission endpoint is available."
                });
                return;
            }
        }

        context.Items["AgentId"] = token.AgentId;
        context.Items["TokenId"] = token.Id;

        if (_strictMtls && !IsMtlsExemptEndpoint(path) && !IsDecommissionEndpoint(context.Request))
        {
            var agentCaService = context.RequestServices.GetService<IAgentCaService>();
            if (agentCaService is not null)
            {
                var (clientCert, ownsCert) = ExtractClientCertificate(context);
                try
                {
                    var mtlsResult = agentCaService.ValidateClientCertificate(clientCert, token.AgentId);
                    if (!mtlsResult.IsValid)
                    {
                        _logger.LogWarning("[AgentAuth] 401 mTLS rejeitado para AgentId={AgentId} ({Path}): {Error}",
                            token.AgentId, path, mtlsResult.Error);
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = mtlsResult.Error ?? "Valid mTLS client certificate required.",
                            code = "MTLS_CERT_REQUIRED"
                        });
                        return;
                    }
                }
                finally
                {
                    if (ownsCert)
                        clientCert?.Dispose();
                }
            }
        }

        await _next(context);
    }

    private bool IsMtlsExemptEndpoint(PathString path)
    {
        if (!path.HasValue)
            return false;
        var value = path.Value!.TrimEnd('/');
        var prefix = _agentAuthPathPrefix.TrimEnd('/');
        return string.Equals(value, prefix + "/me/mtls/enroll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, prefix + "/me/mtls/ca", StringComparison.OrdinalIgnoreCase);
    }

    private static (X509Certificate2? Cert, bool OwnsCert) ExtractClientCertificate(HttpContext context)
    {
        if (context.Connection.ClientCertificate is not null)
            return (context.Connection.ClientCertificate, false);

        var verifyHeader = context.Request.Headers["X-SSL-Client-Verify"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(verifyHeader)
            && !string.Equals(verifyHeader.Trim(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return (null, false);
        }

        var rawHeader = context.Request.Headers["X-SSL-Client-Cert"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawHeader))
            return (null, false);

        try
        {
            var pem = Uri.UnescapeDataString(rawHeader);
            return (X509Certificate2.CreateFromPem(pem), true);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>
    /// O agente está soft-deleted? Consulta tolerante: em falha NÃO bloqueia
    /// (fail-open) — negar acesso a um agente ativo por indisponibilidade do
    /// banco seria pior que o risco de um agente já excluído alcançar um
    /// endpoint por alguns instantes.
    /// </summary>
    private async Task<bool> IsTrashedAgentAsync(Guid agentId, IAgentRepository agentRepo)
    {
        try
        {
            var agent = await agentRepo.GetByIdForDecommissionAsync(agentId);
            return agent?.DeletedAt is not null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AgentAuth] Falha ao verificar lixeira do agente {AgentId}.", agentId);
            return false;
        }
    }

    /// <summary>
    /// Endpoints de AUTO-IDENTIDADE que podem dispensar o header X-Agent-ID: o
    /// agent recuperado só com token precisa de <c>GET /me</c> e
    /// <c>GET /me/configuration</c> para descobrir o próprio AgentId.
    ///
    /// A comparação é por IGUALDADE EXATA do path normalizado
    /// (<c>TrimEnd('/')</c>) contra uma allowlist de dois caminhos montada a
    /// partir do prefixo configurado, case-insensitive. Não usamos
    /// <c>StartsWith</c>/<c>Contains</c> de propósito: um prefixo
    /// "agent-auth/me" liberaria por engano <c>/me/commands</c>,
    /// <c>/me/hardware</c> e subrotas administrativas de outros controllers.
    /// </summary>
    private bool IsSelfIdentityEndpoint(PathString path)
    {
        if (!path.HasValue)
            return false;

        var value = path.Value!.TrimEnd('/');
        var prefix = _agentAuthPathPrefix.TrimEnd('/');

        return string.Equals(value, prefix + "/me", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, prefix + "/me/configuration", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Único endpoint que um agente na lixeira pode chamar: o DELETE do próprio
    /// registro (<c>/api/v1/agent-auth/me</c>), usado pelo descomissionamento.
    /// </summary>
    private bool IsDecommissionEndpoint(HttpRequest request)
    {
        if (!HttpMethods.IsDelete(request.Method) || !request.Path.HasValue)
            return false;

        var value = request.Path.Value!.TrimEnd('/');
        var prefix = _agentAuthPathPrefix.TrimEnd('/');
        return string.Equals(value, prefix + "/me", StringComparison.OrdinalIgnoreCase);
    }
}

public static class AgentAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseAgentAuth(this IApplicationBuilder app)
        => app.UseMiddleware<AgentAuthMiddleware>();
}
