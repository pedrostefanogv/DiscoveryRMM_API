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
/// Usa IPermissionService para checar a permissão no escopo Global.
/// Para escopos mais específicos (Client/Site), use a sobrecarga com scope.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequirePermissionAttribute : Attribute, IFilterFactory
{
    private readonly ResourceType _resource;
    private readonly ActionType _action;

    public RequirePermissionAttribute(ResourceType resource, ActionType action)
    {
        _resource = resource;
        _action = action;
    }

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var permissionService = serviceProvider.GetRequiredService<IPermissionService>();
        return new RequirePermissionFilter(permissionService, _resource, _action);
    }
}

internal class RequirePermissionFilter : IAsyncActionFilter
{
    private readonly IPermissionService _permissionService;
    private readonly ResourceType _resource;
    private readonly ActionType _action;

    public RequirePermissionFilter(IPermissionService permissionService, ResourceType resource, ActionType action)
    {
        _permissionService = permissionService;
        _resource = resource;
        _action = action;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.Items["UserId"] is not Guid userId)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Autenticação necessária." });
            return;
        }

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

        await next();
    }
}
