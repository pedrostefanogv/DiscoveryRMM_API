using System.Reflection;
using Discovery.Api.Controllers;
using Discovery.Api.Filters;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.DTOs.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Guarda de regressão do escopo das operações de ENERGIA EM MASSA do SitesController
/// (todos os agentes do site).
///
/// Contexto: Restart/Shutdown/WakeOnLan usavam o default Global de [RequirePermission],
/// enquanto o Delete (menos destrutivo) já aceitava escopo do cliente via FromRoute.
/// Isso invertia o princípio do menor privilégio: as ações mais destrutivas exigiam o
/// escopo mais amplo. Estes testes travam a correção.
/// </summary>
[TestFixture]
public class SitePowerScopeTests
{
    // ── Wiring dos atributos ─────────────────────────────────────────

    [TestCase(nameof(SitesController.RestartSite))]
    [TestCase(nameof(SitesController.ShutdownSite))]
    [TestCase(nameof(SitesController.WakeOnLanSite))]
    public void Endpoint_de_energia_em_massa_usa_ScopeSource_FromRoute(string actionName)
    {
        var attribute = ScopeOf(actionName);

        Assert.That(attribute, Is.Not.Null, "o endpoint precisa manter o RequirePermission");
        Assert.That(
            ScopeOfField(attribute!),
            Is.EqualTo(ScopeSource.FromRoute),
            "energia em massa não pode exigir atribuição Global: usuários com escopo de " +
            "cliente/site perderiam a operação que o Delete já permite");
    }

    /// <summary>
    /// Trava o LIMITE CONHECIDO documentado no controller: com parâmetros
    /// (clientId, id) e sem "siteId", o resolvedor devolve ScopeLevel.Client —
    /// o {id} do site NÃO é aplicado pelo filtro. Se algum dia a rota ganhar
    /// "siteId", este teste falha e obriga a revisitar a documentação.
    /// </summary>
    [Test]
    public async Task FromRoute_com_clientId_e_id_resolve_para_grao_de_CLIENTE()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        var (scope, scopeId) = await ResolveAsync(
            new Dictionary<string, object?>
            {
                ["clientId"] = clientId.ToString(),
                ["id"] = siteId.ToString(),
            });

        Assert.That(scope, Is.EqualTo(ScopeLevel.Client));
        Assert.That(scopeId, Is.EqualTo(clientId), "o escopo aplicado é o do CLIENTE da rota");
        Assert.That(scopeId, Is.Not.EqualTo(siteId), "o id do site não é usado pelo filtro");
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static RequirePermissionAttribute? ScopeOf(string actionName)
    {
        var method = typeof(SitesController)
            .GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, $"ação {actionName} não encontrada");

        return method!
            .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: false)
            .Cast<RequirePermissionAttribute>()
            .SingleOrDefault();
    }

    // O escopo fica em campo privado do atributo (não há getter público).
    private static ScopeSource ScopeOfField(RequirePermissionAttribute attribute)
    {
        var field = typeof(RequirePermissionAttribute)
            .GetField("_scopeSource", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(field, Is.Not.Null);
        return (ScopeSource)field!.GetValue(attribute)!;
    }

    private sealed class RecordingPermissionService : IPermissionService
    {
        public ScopeLevel? LastScope;
        public Guid? LastScopeId;

        public Task<bool> HasPermissionAsync(Guid userId, ResourceType resource, ActionType action,
            ScopeLevel scopeLevel = ScopeLevel.Global, Guid? scopeId = null, Guid? parentScopeId = null)
        {
            LastScope = scopeLevel;
            LastScopeId = scopeId;
            return Task.FromResult(true);
        }

        public Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess());

        public Task InvalidateUserCacheAsync(Guid userId) => Task.CompletedTask;
        public Task InvalidateAllCacheAsync() => Task.CompletedTask;
    }

    private sealed class SitesControllerStub : ControllerBase;

    private static async Task<(ScopeLevel? Scope, Guid? ScopeId)> ResolveAsync(
        Dictionary<string, object?> routeValues)
    {
        var svc = new RecordingPermissionService();
        var http = new DefaultHttpContext();
        http.Items["UserId"] = Guid.NewGuid();

        var routeData = new RouteData();
        foreach (var (k, v) in routeValues) routeData.Values[k] = v;

        var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
        var ctx = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new SitesControllerStub());

        var filter = new RequirePermissionFilter(
            svc,
            new Discovery.Infrastructure.Services.ScopeContext(svc),
            ResourceType.Agents,
            ActionType.Execute,
            ScopeSource.FromRoute);

        await filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));
        return (svc.LastScope, svc.LastScopeId);
    }
}
