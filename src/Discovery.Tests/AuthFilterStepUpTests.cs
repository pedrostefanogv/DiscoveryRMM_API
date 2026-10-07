using Discovery.Api.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Guardas aplicadas na revisão dos residuais:
/// - o token de step-up NÃO pode valer como token de sessão (senão abriria toda a API);
/// - operações sensíveis de MFA exigem step-up, exceto no onboarding (mfa_setup).
/// </summary>
[TestFixture]
public class AuthFilterStepUpTests
{
    private static (ActionExecutingContext Context, ActionDescriptor Descriptor) NewContext()
    {
        var httpContext = new DefaultHttpContext();
        var descriptor = new ActionDescriptor();
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var context = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());

        return (context, descriptor);
    }

    private static async Task<(bool Executed, IActionResult? Result)> RunAsync(
        IAsyncActionFilter filter, ActionExecutingContext context)
    {
        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        return (executed, context.Result);
    }

    [Test]
    public async Task RequireUserAuth_RejectsStepUpToken()
    {
        var (context, _) = NewContext();
        context.HttpContext.Items["UserId"] = Guid.NewGuid();
        context.HttpContext.Items["StepUp"] = true;

        var (executed, result) = await RunAsync(new RequireUserAuthFilter(), context);

        Assert.That(executed, Is.False, "token de step-up não pode autorizar endpoints de sessão");
        Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    [Test]
    public async Task RequireUserAuth_AllowsFullSession()
    {
        var (context, _) = NewContext();
        context.HttpContext.Items["UserId"] = Guid.NewGuid();
        context.HttpContext.Items["MfaVerified"] = true;

        var (executed, result) = await RunAsync(new RequireUserAuthFilter(), context);

        Assert.That(executed, Is.True);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task RequireUserAuth_StillRejectsPendingAndSetupTokens()
    {
        foreach (var flag in new[] { "MfaPending", "MfaSetup" })
        {
            var (context, _) = NewContext();
            context.HttpContext.Items["UserId"] = Guid.NewGuid();
            context.HttpContext.Items[flag] = true;

            var (executed, result) = await RunAsync(new RequireUserAuthFilter(), context);

            Assert.That(executed, Is.False, flag);
            Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>(), flag);
        }
    }

    [Test]
    public async Task RequireUserAuth_HonoursAllowAnonymous()
    {
        var (context, descriptor) = NewContext();
        descriptor.EndpointMetadata = new List<object> { new AllowAnonymousAttribute() };
        context.HttpContext.Items["StepUp"] = true;

        var (executed, _) = await RunAsync(new RequireUserAuthFilter(), context);

        Assert.That(executed, Is.True);
    }

    [Test]
    public async Task RequireMfaStepUp_AllowsStepUpAndOnboardingTokens()
    {
        foreach (var flag in new[] { "StepUp", "MfaSetup" })
        {
            var (context, _) = NewContext();
            context.HttpContext.Items["UserId"] = Guid.NewGuid();
            context.HttpContext.Items[flag] = true;

            var (executed, _) = await RunAsync(new RequireMfaStepUpFilter(), context);

            Assert.That(executed, Is.True, flag);
        }
    }

    [Test]
    public async Task RequireMfaStepUp_BlocksPlainSession()
    {
        var (context, _) = NewContext();
        context.HttpContext.Items["UserId"] = Guid.NewGuid();

        var (executed, result) = await RunAsync(new RequireMfaStepUpFilter(), context);

        Assert.That(executed, Is.False);
        Assert.That(result, Is.InstanceOf<ObjectResult>());
    }
}
