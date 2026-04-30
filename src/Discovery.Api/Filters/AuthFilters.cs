using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Discovery.Api.Filters;

/// <summary>
/// Garante que a requisição possui uma sessão de usuário válida
/// (JWT de sessão completa OU autenticação via API token).
/// Tokens mfa_pending e mfa_setup são REJEITADOS aqui.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class RequireUserAuthAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new RequireUserAuthFilter();
}

internal class RequireUserAuthFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Permite opt-out explícito para rotas públicas/fluxos com autenticação própria.
        if (context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            await next();
            return;
        }

        var items = context.HttpContext.Items;

        if (items["UserId"] is not Guid)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Autenticação necessária." });
            return;
        }

        // Tokens de pendência MFA não podem acessar endpoints protegidos
        if (items["MfaPending"] is true || items["MfaSetup"] is true)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Autenticação MFA ainda não concluída." });
            return;
        }

        await next();
    }
}

/// <summary>
/// Garante que o JWT seja um mfaPendingToken (claim mfa_pending=true).
/// Usado nos endpoints de verificação MFA (segunda etapa do login).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class RequireMfaPendingAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new RequireMfaPendingFilter();
}

internal class RequireMfaPendingFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var items = context.HttpContext.Items;

        if (items["UserId"] is not Guid)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Token MFA pendente necessário." });
            return;
        }

        if (items["MfaPending"] is not true)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Este endpoint requer um token mfa_pending." });
            return;
        }

        await next();
    }
}

/// <summary>
/// Garante que o JWT seja um mfaSetupToken (claim mfa_setup=true).
/// Usado nos endpoints de registro do primeiro fator MFA.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class RequireMfaSetupAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new RequireMfaSetupFilter();
}

internal class RequireMfaSetupFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var items = context.HttpContext.Items;

        if (items["UserId"] is not Guid)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Token de setup MFA necessário." });
            return;
        }

        if (items["MfaSetup"] is not true)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Este endpoint requer um token mfa_setup." });
            return;
        }

        await next();
    }
}

/// <summary>
/// Garante que o JWT seja um mfaSetupToken OU uma sessão completa.
/// Usado nos endpoints de adição de nova chave MFA (tanto setup inicial quanto adição posterior).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class RequireMfaSetupOrFullSessionAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new RequireMfaSetupOrFullSessionFilter();
}

internal class RequireMfaSetupOrFullSessionFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var items = context.HttpContext.Items;

        if (items["UserId"] is not Guid)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Autenticação necessária." });
            return;
        }

        // Somente mfa_pending é bloqueado: mfa_setup e sessão completa são permitidos
        if (items["MfaPending"] is true)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Autenticação MFA ainda não concluída." });
            return;
        }

        await next();
    }
}

/// <summary>
/// Verifica se o usuário possui a permissão especificada.
/// Por padrão, usa escopo Global. Para escopos com resolução via rota, use RequirePermission(ResourceType.X, ActionType.Y, ScopeSource.FromRoute).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequirePermissionAttribute : Attribute, IFilterFactory
{
    private readonly ResourceType _resource;
    private readonly ActionType _action;
    private readonly ScopeSource _scopeSource;

    public RequirePermissionAttribute(ResourceType resource, ActionType action)
    {
        _resource = resource;
        _action = action;
        _scopeSource = ScopeSource.Global;
    }

    public RequirePermissionAttribute(ResourceType resource, ActionType action, ScopeSource scopeSource)
    {
        _resource = resource;
        _action = action;
        _scopeSource = scopeSource;
    }

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var permissionService = serviceProvider.GetRequiredService<IPermissionService>();
        return new RequirePermissionFilter(permissionService, _resource, _action, _scopeSource);
    }
}

internal class RequirePermissionFilter : IAsyncActionFilter
{
    private readonly IPermissionService _permissionService;
    private readonly ResourceType _resource;
    private readonly ActionType _action;
    private readonly ScopeSource _scopeSource;

    private static readonly HashSet<string> EntityIdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id"
    };

    private static readonly HashSet<string> ClientIdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "clientId"
    };
    private static readonly HashSet<string> SiteIdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "siteId"
    };

    public RequirePermissionFilter(IPermissionService permissionService, ResourceType resource, ActionType action, ScopeSource scopeSource = ScopeSource.Global)
    {
        _permissionService = permissionService;
        _resource = resource;
        _action = action;
        _scopeSource = scopeSource;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.Items["UserId"] is not Guid userId)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Autenticação necessária." });
            return;
        }

        if (_scopeSource == ScopeSource.Global)
        {
            var hasPermission = await _permissionService.HasPermissionAsync(
                userId, _resource, _action, ScopeLevel.Global, null, null);

            if (!hasPermission)
            {
                context.Result = new ObjectResult(new { message = "Permissão insuficiente." })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                return;
            }
        }
        else // ScopeSource.FromRoute
        {
            var (scopeLevel, scopeId, parentScopeId) = ResolveScopeFromRoute(context);
            var hasPermission = await _permissionService.HasPermissionAsync(
                userId, _resource, _action, scopeLevel, scopeId, parentScopeId);

            if (!hasPermission)
            {
                context.Result = new ObjectResult(new { message = "Permissão insuficiente para este escopo." })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                return;
            }

            // Popula HttpContext.Items para uso futuro (ex.: queries filtradas)
            context.HttpContext.Items["ClientId"] = scopeLevel == ScopeLevel.Client ? scopeId : parentScopeId;
            context.HttpContext.Items["SiteId"] = scopeLevel == ScopeLevel.Site ? scopeId : null;
        }

        await next();
    }

    /// <summary>
    /// Resolve o escopo a partir dos parâmetros da rota.
    /// 
    /// Heurística:
    /// - Se a rota tem "siteId", verifica Site (com parentScopeId = clientId da rota)
    /// - Se a rota tem "clientId", verifica Client
    /// - Se a rota tem apenas "id" e o controller termina com "ClientsController" ou "SitesController", trata como Client
    /// </summary>
    private static (ScopeLevel, Guid?, Guid?) ResolveScopeFromRoute(ActionExecutingContext context)
    {
        var routeValues = context.RouteData.Values;

        // Tenta siteId primeiro (escopo mais específico)
        if (TryGetRouteGuid(routeValues, SiteIdKeys, out var siteId))
        {
            // parentScopeId = clientId (da rota ou do HttpContext.Items)
            Guid? parentClientId = null;
            if (TryGetRouteGuid(routeValues, ClientIdKeys, out var clientId))
                parentClientId = clientId;
            else if (context.HttpContext.Items["ClientId"] is Guid ctxClientId)
                parentClientId = ctxClientId;

            return (ScopeLevel.Site, siteId, parentClientId);
        }

        // Tenta clientId (escopo intermediário)
        if (TryGetRouteGuid(routeValues, ClientIdKeys, out var resolvedClientId))
        {
            return (ScopeLevel.Client, resolvedClientId, null);
        }

        // Heurística: se o controller for ClientsController/SitesController e tiver {id},
        // trata como operação com escopo específico
        var controllerTypeName = context.Controller.GetType().Name;
        if (TryGetRouteGuid(routeValues, EntityIdKeys, out var entityId))
        {
            if (controllerTypeName.EndsWith("ClientsController", StringComparison.OrdinalIgnoreCase))
            {
                return (ScopeLevel.Client, entityId, null);
            }
        }

        // Fallback: escopo global (rota sem identificadores de cliente/site)
        return (ScopeLevel.Global, null, null);
    }

    private static bool TryGetRouteGuid(IDictionary<string, object?> routeValues, HashSet<string> keys, out Guid value)
    {
        foreach (var key in keys)
        {
            if (routeValues.TryGetValue(key, out var raw) && raw is string s && Guid.TryParse(s, out value))
                return true;
            if (routeValues.TryGetValue(key, out raw) && raw is Guid g)
            {
                value = g;
                return true;
            }
        }
        value = default;
        return false;
    }
}
