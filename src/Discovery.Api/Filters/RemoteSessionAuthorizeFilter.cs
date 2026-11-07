using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Discovery.Api.Filters;

/// <summary>
/// Filtro de autorização para endpoints de sessão remota.
/// Valida que o usuário tem permissão RemoteSession.Create/Execute/View sobre o agent.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class RemoteSessionAuthorizeAttribute : Attribute, IFilterFactory
{
    public ActionType RequiredAction { get; init; } = ActionType.Execute;

    /// <summary>
    /// Aceita sessões em pending_consent (status, cancelar espera). Endpoints que
    /// exigem mídia/credenciais continuam com o default false.
    /// </summary>
    public bool AllowPending { get; init; }

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new RemoteSessionAuthorizeFilter(
            serviceProvider.GetRequiredService<IRemoteSessionManager>(),
            serviceProvider.GetRequiredService<ILogger<RemoteSessionAuthorizeFilter>>(),
            serviceProvider.GetRequiredService<Services.RemoteSessionScopeResolver>(),
            RequiredAction,
            AllowPending);
}

internal class RemoteSessionAuthorizeFilter : IAsyncActionFilter
{
    private readonly IRemoteSessionManager _sessionManager;
    private readonly ILogger<RemoteSessionAuthorizeFilter> _logger;
    private readonly Services.RemoteSessionScopeResolver _scopeResolver;
    private readonly ActionType _requiredAction;
    private readonly bool _allowPending;

    public RemoteSessionAuthorizeFilter(
        IRemoteSessionManager sessionManager,
        ILogger<RemoteSessionAuthorizeFilter> logger,
        Services.RemoteSessionScopeResolver scopeResolver,
        ActionType requiredAction,
        bool allowPending)
    {
        _sessionManager = sessionManager;
        _logger = logger;
        _scopeResolver = scopeResolver;
        _requiredAction = requiredAction;
        _allowPending = allowPending;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Extrai agentId e sessionId da rota
        var routeAgentId = context.RouteData.Values["agentId"]?.ToString();
        var routeSessionId = context.RouteData.Values["sessionId"]?.ToString();
        var userId = ResolveUserId(context.HttpContext);

        if (!Guid.TryParse(routeAgentId, out var agentId))
        {
            context.Result = new BadRequestObjectResult(new { error = "Invalid agentId." });
            return;
        }

        // ESCOPO (client/site): este filtro antes validava apenas a POSSE da sessão
        // pelo usuário. Como o RequirePermission dos endpoints só prova que a
        // permissão existe em ALGUM lugar, um técnico escopado ao cliente A
        // conseguia operar sessões/controles no cliente B bastando conhecer o
        // sessionId/agentId. O escopo é recortado aqui, antes de qualquer trabalho.
        //
        // ORDEM DOS FILTROS: este atributo é declarado ANTES de RequirePermission,
        // então a checagem de escopo roda primeiro. Por isso ela NÃO decide quando o
        // usuário simplesmente não tem a permissão: nesse caso quem responde é o
        // RequirePermission, com 403 ("Permissão insuficiente"). Se respondêssemos
        // 404 aqui, um usuário sem Agents:Execute receberia "não encontrado" em vez
        // de "permissão insuficiente" — diagnóstico enganoso e mudança de contrato.
        //
        // A decisão de escopo só é aplicada quando o usuário POSSUI algum acesso a
        // Agents: aí sim 404 (e não 403), para não confirmar a existência do
        // agente/sessão de outro tenant — mesma decisão de
        // AuthorizeTerminalRecordingAsync.
        if (userId != Guid.Empty)
        {
            var isExecute = _requiredAction == ActionType.Execute;
            var decision = await _scopeResolver.EvaluateAgentAsync(
                userId, agentId, isExecute, context.HttpContext.RequestAborted);

            if (decision == Services.RemoteSessionScopeDecision.DeniedOutOfScope)
            {
                _logger.LogWarning(
                    "[RemoteSessionAuthorize] Acesso negado por ESCOPO: User={UserId}, AgentId={AgentId}, RequiredAction={Action}",
                    userId, agentId, _requiredAction);

                context.Result = new NotFoundObjectResult(new
                {
                    error = "Remote session not found.",
                    reason = "out_of_scope"
                });
                return;
            }

            // WithoutResourceAccess: deixa o RequirePermission responder (403).
            // AgentMissing cai no fluxo abaixo, que já trata sessão/agente ausente.
        }

        // Se tem sessionId na rota, valida que a sessão pertence ao usuário
        if (Guid.TryParse(routeSessionId, out var sessionId) && userId != Guid.Empty)
        {
            try
            {
                var session = await _sessionManager.GetForUserAsync(
                    sessionId, userId, _allowPending, context.HttpContext.RequestAborted);
                if (session is null)
                {
                    // Diagnóstico: buscar a sessão bruta para entender o motivo da falha
                    var rawSession = await _sessionManager.GetRawSessionAsync(sessionId, context.HttpContext.RequestAborted);
                    if (rawSession is null)
                    {
                        context.Result = new UnauthorizedObjectResult(new
                        {
                            error = "Remote session not found.",
                            sessionId = sessionId,
                            reason = "session_does_not_exist"
                        });
                    }
                    else if (rawSession.Status == Discovery.Core.Enums.RemoteSessionStatus.PendingConsent && !_allowPending)
                    {
                        context.Result = new ObjectResult(new
                        {
                            error = "Remote session is waiting for user consent.",
                            sessionId = sessionId,
                            status = rawSession.Status,
                            reason = "consent_pending"
                        })
                        { StatusCode = StatusCodes.Status409Conflict };
                    }
                    else if (rawSession.Status != "active")
                    {
                        context.Result = new UnauthorizedObjectResult(new
                        {
                            error = "Remote session is no longer active.",
                            sessionId = sessionId,
                            status = rawSession.Status,
                            reason = "session_not_active"
                        });
                    }
                    else if (rawSession.UserId == Guid.Empty)
                    {
                        _logger.LogError(
                            "[RemoteSessionAuthorize] Sessão ÓRFÃ detectada: SessionId={SessionId}, AgentId={AgentId}, " +
                            "RequestUserId={RequestUserId}. Possível bug na criação da sessão.",
                            sessionId, rawSession.AgentId, userId);
                        context.Result = new UnauthorizedObjectResult(new
                        {
                            error = "Remote session is orphaned (created without a valid user). This is a server-side bug — please report it.",
                            sessionId = sessionId,
                            reason = "session_orphaned"
                        });
                    }
                    else if (rawSession.UserId != userId)
                    {
                        _logger.LogWarning(
                            "[RemoteSessionAuthorize] UserId mismatch: SessionUserId={SessionUserId}, RequestUserId={RequestUserId}, SessionId={SessionId}",
                            rawSession.UserId, userId, sessionId);
                        context.Result = new UnauthorizedObjectResult(new
                        {
                            error = "Remote session belongs to a different user.",
                            sessionId = sessionId,
                            reason = "user_mismatch"
                        });
                    }
                    else
                    {
                        context.Result = new UnauthorizedObjectResult(new
                        {
                            error = "Remote session not found, not active, or you don't have access to it.",
                            sessionId = sessionId,
                            reason = "unknown"
                        });
                    }
                    return;
                }

                if (session.AgentId != agentId)
                {
                    context.Result = new BadRequestObjectResult(new
                    {
                        error = "Session does not belong to the specified agent.",
                        sessionId = sessionId,
                        sessionAgentId = session.AgentId,
                        requestAgentId = agentId
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating remote session {SessionId} for user {UserId}", sessionId, userId);
                context.Result = new StatusCodeResult(500);
                return;
            }
        }

        await next();
    }

    private static Guid ResolveUserId(HttpContext httpContext)
    {
        if (httpContext.Items["UserId"] is Guid uid) return uid;
        if (httpContext.Items["UserId"] is string uidStr && Guid.TryParse(uidStr, out var parsed)) return parsed;
        return Guid.Empty;
    }
}
