using System.Reflection;
using Microsoft.AspNetCore.SignalR;

namespace Discovery.Api.Hubs;

/// <summary>
/// Filtro de autorização para AgentHub — equivalente ao default_permissions do NATS.
///
/// NATS: JWT define "agent X pode publicar só em tenant.{c}.{s}.{X}.*"
/// SignalR: este filtro define "agent X pode chamar só métodos do seu próprio escopo"
///
/// Regras:
///   1. Agents NÃO podem chamar métodos de usuário
///   2. Usuários NÃO podem chamar métodos de agent
///   3. Agents NÃO podem invocar métodos passando agentId de outro agente
/// </summary>
public class AgentHubAuthorizationFilter : IHubFilter
{
    private readonly ILogger<AgentHubAuthorizationFilter> _logger;

    private static readonly HashSet<string> AgentOnlyMethods =
    [
        nameof(AgentHub.RegisterAgent),
        nameof(AgentHub.Heartbeat),
        nameof(AgentHub.CommandResult),
        nameof(AgentHub.SecureHandshakeAsync),
        nameof(AgentHub.PushRemoteDebugLog),
    ];

    private static readonly HashSet<string> UserOnlyMethods =
    [
        nameof(AgentHub.JoinDashboard),
        nameof(AgentHub.JoinClientDashboard),
        nameof(AgentHub.JoinSiteDashboard),
    ];

    private static readonly HashSet<string> MethodsWithAgentIdParameter =
    [
        nameof(AgentHub.RegisterAgent),
        nameof(AgentHub.Heartbeat),
    ];

    // Cache de parâmetros por método (reflection única)
    private static readonly Dictionary<string, ParameterInfo[]> MethodParametersCache = new();

    public AgentHubAuthorizationFilter(ILogger<AgentHubAuthorizationFilter> logger)
    {
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var methodName = invocationContext.HubMethodName;
        var context = invocationContext.Context;
        var isAgent = context.Items["AgentId"] is Guid;
        var isUser = context.Items["UserId"] is Guid;

        // ── Regra 1: Agent chamando método de usuário ────────────────────────
        if (isAgent && UserOnlyMethods.Contains(methodName))
        {
            _logger.LogWarning(
                "SignalR REJECTED: Agent tried to call user-only method '{Method}'.",
                methodName);
            throw new HubException("Access denied. This method is for users only.");
        }

        // ── Regra 2: Usuário chamando método de agent ───────────────────────
        if (isUser && AgentOnlyMethods.Contains(methodName))
        {
            _logger.LogWarning(
                "SignalR REJECTED: User tried to call agent-only method '{Method}'.",
                methodName);
            throw new HubException("Access denied. This method is for agents only.");
        }

        // ── Regra 3: Agent tentando se passar por outro agent ────────────────
        if (isAgent && MethodsWithAgentIdParameter.Contains(methodName))
        {
            var authenticatedId = (Guid)context.Items["AgentId"]!;
            var providedAgentId = ExtractAgentId(invocationContext, methodName);

            if (providedAgentId.HasValue && providedAgentId.Value != authenticatedId)
            {
                _logger.LogWarning(
                    "SignalR REJECTED: Agent {AuthenticatedId} tried to impersonate agent {SpoofedId} via '{Method}'. " +
                    "Equivalent to an agent publishing on another agent's NATS subject.",
                    authenticatedId,
                    providedAgentId.Value,
                    methodName);
                throw new HubException("Access denied. Agent identity mismatch.");
            }
        }

        // ── Regra 4: Anônimo tentando chamar método de agent ────────────────
        if (!isAgent && !isUser && AgentOnlyMethods.Contains(methodName))
        {
            _logger.LogWarning(
                "SignalR REJECTED: Unauthenticated caller tried to call '{Method}'.",
                methodName);
            throw new HubException("Authentication required.");
        }

        return await next(invocationContext);
    }

    /// <summary>
    /// Extrai o valor do parâmetro 'agentId' da invocação, pelo índice do parâmetro.
    /// </summary>
    private static Guid? ExtractAgentId(HubInvocationContext context, string methodName)
    {
        var parameters = GetMethodParameters(methodName);
        if (parameters is null) return null;

        for (int i = 0; i < parameters.Length && i < context.HubMethodArguments.Count; i++)
        {
            if (!string.Equals(parameters[i].Name, "agentId", StringComparison.OrdinalIgnoreCase))
                continue;

            var arg = context.HubMethodArguments[i];
            if (arg is Guid guidValue)
                return guidValue;

            if (arg is string strValue && Guid.TryParse(strValue, out var parsed))
                return parsed;

            return null;
        }

        return null;
    }

    private static ParameterInfo[]? GetMethodParameters(string methodName)
    {
        if (MethodParametersCache.TryGetValue(methodName, out var cached))
            return cached;

        var method = typeof(AgentHub).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (method is null) return null;

        var parameters = method.GetParameters();
        MethodParametersCache[methodName] = parameters;
        return parameters;
    }
}
