using System.Reflection;
using Discovery.Api.Controllers;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Tests;

/// <summary>
/// BUG 2026-11-21 (tupa.pstefano.com.br): o portal chama
/// POST /api/v1/nats-auth/user/credentials desde sempre
/// (DiscoveryRMM_Site/src/api/nats.ts) e a API respondia 404 — a rota nunca
/// existiu. Consequencia: o dashboard mostrava "Falha de rede NATS"/"Offline"
/// com o broker, o nginx e o tunnel saudaveis (o painel "Testar" respondia
/// "Conectado").
///
/// Este teste trava o CONTRATO da rota: se alguem renomear o metodo ou mudar o
/// verbo/path, o portal perde a conexao de novo — em silencio.
/// </summary>
public class NatsAuthControllerRouteTests
{
    private static (string? Template, string[] Verbs) RouteOf(string methodName)
    {
        var method = typeof(NatsAuthController).GetMethod(methodName);
        Assert.That(method, Is.Not.Null, $"metodo {methodName} nao existe no NatsAuthController");

        var http = method!.GetCustomAttribute<HttpPostAttribute>();
        var verbs = http is null ? Array.Empty<string>() : new[] { "POST" };
        return (http?.Template, verbs);
    }

    [Test]
    public void IssueUserCredentials_IsAPostAtUserCredentials()
    {
        var (template, verbs) = RouteOf(nameof(NatsAuthController.IssueUserCredentials));

        Assert.That(verbs, Does.Contain("POST"),
            "o portal faz POST em /api/v1/nats-auth/user/credentials");
        Assert.That(template, Is.EqualTo("user/credentials"),
            "o path relativo precisa continuar sendo user/credentials");
    }

    [Test]
    public void GetStatus_StillExists()
    {
        var method = typeof(NatsAuthController).GetMethod(nameof(NatsAuthController.GetStatus));
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.GetCustomAttribute<HttpGetAttribute>(), Is.Not.Null);
    }

    [Test]
    public async Task IssueUserCredentials_SetsScopeContextUserIdBeforeResolvingAccess()
    {
        var expectedUserId = Guid.NewGuid();
        var permissionService = new StubGlobalDashboardPermissionService(expectedUserId);
        var scopeContext = new ScopeContext(permissionService);
        var credentialsService = new CapturingNatsCredentialsService();

        var controller = new NatsAuthController(null!, credentialsService, scopeContext)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["UserId"] = expectedUserId }
                }
            }
        };

        var result = await controller.IssueUserCredentials(null, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(scopeContext.UserId, Is.EqualTo(expectedUserId));
        Assert.That(credentialsService.CapturedAccess, Is.Not.Null);
        Assert.That(credentialsService.CapturedAccess!.HasGlobalAccess, Is.True);
    }

    private sealed class StubGlobalDashboardPermissionService(Guid expectedUserId) : IPermissionService
    {
        public Task<bool> HasPermissionAsync(
            Guid userId,
            ResourceType resource,
            ActionType action,
            ScopeLevel scopeLevel = ScopeLevel.Global,
            Guid? scopeId = null,
            Guid? parentScopeId = null) => Task.FromResult(true);

        public Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
        {
            if (userId == expectedUserId && resource == ResourceType.Dashboard && action == ActionType.View)
                return Task.FromResult(new UserScopeAccess { HasGlobalAccess = true });

            return Task.FromResult(new UserScopeAccess { HasGlobalAccess = false });
        }

        public Task InvalidateUserCacheAsync(Guid userId) => Task.CompletedTask;
        public Task InvalidateAllCacheAsync() => Task.CompletedTask;
    }

    private sealed class CapturingNatsCredentialsService : INatsCredentialsService
    {
        public UserScopeAccess? CapturedAccess { get; private set; }

        public Task<NatsCredentialsResponse> IssueForUserAsync(
            Guid userId,
            UserScopeAccess scopeAccess,
            Guid? clientId,
            Guid? siteId,
            CancellationToken ct = default,
            UserScopeAccess? remoteDebugScopeAccess = null,
            IReadOnlyList<string>? remoteDebugPublishSubjects = null,
            IReadOnlyList<string>? remoteDebugSubscribeSubjects = null)
        {
            CapturedAccess = scopeAccess;
            return Task.FromResult(new NatsCredentialsResponse(
                Jwt: "jwt",
                NkeySeed: "seed",
                PublicKey: "pub",
                ExpiresAtUtc: DateTime.UtcNow.AddMinutes(15),
                PublishSubjects: Array.Empty<string>(),
                SubscribeSubjects: new[]
                {
                    "tenant.*.site.*.dashboard.events",
                    "tenant.*.dashboard.events",
                    "tenant.unscoped.dashboard.events"
                }));
        }

        public Task<NatsCredentialsResponse> IssueForAgentAsync(Guid agentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(string Jwt, DateTime ExpiresAtUtc)> IssueUserJwtForAgentAsync(
            string userPublicKey, Guid agentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(string Jwt, DateTime ExpiresAtUtc)> IssueUserJwtForUserAsync(
            string userPublicKey, Guid userId, UserScopeAccess scopeAccess, CancellationToken ct = default, UserScopeAccess? remoteDebugScopeAccess = null)
            => throw new NotSupportedException();

        public Task<(string Jwt, string NkeySeed, DateTime ExpiresAtUtc)> IssueSessionCredentialsAsync(
            string[] publishSubjects, string[] subscribeSubjects, int ttlMinutes, string traceLabel, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(string Jwt, DateTime ExpiresAtUtc)> IssueSessionJwtForPublicKeyAsync(
            string userPublicKey, string[] publishSubjects, string[] subscribeSubjects, int ttlMinutes, string traceLabel, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
