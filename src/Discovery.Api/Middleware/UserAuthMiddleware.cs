using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

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

    public async Task InvokeAsync(HttpContext context, IJwtService jwtService, IUserSessionRepository sessionRepo)
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
                    // C5: publica o principal no HttpContext.User também — hoje a
                    // identidade só existia em HttpContext.Items, o que impedia o uso
                    // de [Authorize]/policies/IAuthorizationService em qualquer ponto
                    // do pipeline (filtros, SignalR, minimal APIs).
                    context.User = principal;
                    context.Items["UserId"] = userId;
                    context.Items["Username"] = principal.FindFirstValue("unique_name")
                        ?? principal.FindFirstValue(ClaimTypes.Name)
                        ?? principal.FindFirstValue("name");
                    context.Items["MfaPending"] = principal.FindFirstValue("mfa_pending") == "true";
                    context.Items["MfaSetup"] = principal.FindFirstValue("mfa_setup") == "true";
                    context.Items["MfaVerified"] = principal.FindFirstValue("mfa_verified") == "true";
                    context.Items["StepUp"] = principal.FindFirstValue("step_up") == "true";

                    var jti = principal.FindFirstValue("jti");
                    if (!string.IsNullOrEmpty(jti))
                    {
                        // Token de sessão usa o jti como SessionId; o token de pendência de MFA
                        // também tem jti, mas ele identifica o desafio (controle de uso único).
                        if (context.Items["MfaPending"] is true)
                            context.Items["MfaTokenId"] = jti;
                        else
                            context.Items["SessionId"] = jti;

                        // C6: revogação precisa valer para o ACCESS token, não só para o
                        // refresh. Sem esta checagem, um logout/troca de senha deixa o access
                        // token válido por até AccessTokenExpirationMinutes (60 min).
                        // jti só é SessionId em token de sessão completa (mfa_pending mapeia
                        // para MfaTokenId e mfa_setup/step_up não têm jti).
                        // BUGFIX: tokens temporários (mfa_pending) têm jti = id do DESAFIO,
                        // não de sessão. Validar esse jti em user_sessions sempre falhava
                        // (GetByIdAsync retorna null) e rejeitava TODA asserção de MFA com
                        // 401 session_revoked antes do filtro RequireMfaPending rodar.
                        var isTemporaryToken = context.Items["MfaPending"] is true
                            || context.Items["MfaSetup"] is true
                            || context.Items["StepUp"] is true;
                        if (!isTemporaryToken && Guid.TryParse(jti, out var sessionId))
                        {
                            var session = await sessionRepo.GetByIdAsync(sessionId);
                            var tokenHash = Convert.ToBase64String(
                                SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                            if (session is null || !session.IsValid || session.AccessTokenHash != tokenHash)
                            {
                                _logger.LogWarning(
                                    "UserAuthMiddleware: sessão {SessionId} inválida/revogada ou access token obsoleto. Path={Path}",
                                    sessionId, context.Request.Path.Value);
                                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                await context.Response.WriteAsJsonAsync(new
                                {
                                    message = "Sessão inválida ou revogada. Faça login novamente.",
                                    code = "session_revoked"
                                });
                                return;
                            }
                        }
                    }

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
