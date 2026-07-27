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

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new RemoteSessionAuthorizeFilter(
            serviceProvider.GetRequiredService<IRemoteSessionManager>(),
            serviceProvider.GetRequiredService<ILogger<RemoteSessionAuthorizeFilter>>(),
            RequiredAction);
}

internal class RemoteSessionAuthorizeFilter : IAsyncActionFilter
{
    private readonly IRemoteSessionManager _sessionManager;
    private readonly ILogger<RemoteSessionAuthorizeFilter> _logger;
    private readonly ActionType _requiredAction;

    public RemoteSessionAuthorizeFilter(
        IRemoteSessionManager sessionManager,
        ILogger<RemoteSessionAuthorizeFilter> logger,
        ActionType requiredAction)
    {
        _sessionManager = sessionManager;
        _logger = logger;
        _requiredAction = requiredAction;
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

        // Se tem sessionId na rota, valida que a sessão pertence ao usuário
        if (Guid.TryParse(routeSessionId, out var sessionId) && userId != Guid.Empty)
        {
            try
            {
                var session = await _sessionManager.GetActiveForUserAsync(sessionId, userId, context.HttpContext.RequestAborted);
                if (session is null)
                {
                    context.Result = new UnauthorizedObjectResult(new
                    {
                        error = "Remote session not found, not active, or you don't have access to it."
                    });
                    return;
                }

                if (session.AgentId != agentId)
                {
                    context.Result = new BadRequestObjectResult(new
                    {
                        error = "Session does not belong to the specified agent."
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
