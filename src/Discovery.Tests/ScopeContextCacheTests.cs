using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressão do bug encontrado na revisão pós-implementação (2026-11-07).
///
/// O <see cref="ScopeContext"/> cacheava por (recurso, ação) SEM o usuário. Como o
/// serviço é Scoped (uma instância por request), um request que resolvesse escopo
/// para duas pessoas servia a segunda com o resultado da PRIMEIRA.
///
/// Caminho real no código: o filtro RequirePermission(..., AccessList) chama
/// GetAccessAsync para o ator autenticado; o handler de atribuição de papel chama
/// SetUserId(RequestedByUserId) e pede o MESMO par (Users, Edit). Com os dois
/// valores iguais (hoje) nada quebra — mas qualquer caller que passe outro usuário
/// (auditoria em nome de, impersonation, job) recebia uma decisão de autorização
/// baseada no escopo da pessoa errada.
///
/// Este teste falha na implementação antiga e passa na corrigida.
/// </summary>
[TestFixture]
public class ScopeContextCacheTests
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Devolve escopo diferente por usuário, para expor o vazamento de cache.</summary>
    private sealed class PerUserPermissionService : IPermissionService
    {
        public int Calls { get; private set; }

        public Task<bool> HasPermissionAsync(Guid userId, ResourceType resource, ActionType action,
            ScopeLevel scopeLevel = ScopeLevel.Global, Guid? scopeId = null, Guid? parentScopeId = null)
            => Task.FromResult(true);

        public Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
        {
            Calls++;
            if (userId == UserA)
            {
                return Task.FromResult(new UserScopeAccess { AllowedClientIds = [ClientA] });
            }

            if (userId == UserB)
            {
                return Task.FromResult(new UserScopeAccess { AllowedClientIds = [ClientB] });
            }

            return Task.FromResult(new UserScopeAccess());
        }

        public Task InvalidateUserCacheAsync(Guid userId) => Task.CompletedTask;
        public Task InvalidateAllCacheAsync() => Task.CompletedTask;
    }

    [Test]
    public async Task Switching_user_does_not_serve_the_previous_users_scope()
    {
        var permissions = new PerUserPermissionService();
        var context = new ScopeContext(permissions);

        context.SetUserId(UserA);
        var accessA = await context.GetAccessAsync(ResourceType.Users, ActionType.Edit);

        context.SetUserId(UserB);
        var accessB = await context.GetAccessAsync(ResourceType.Users, ActionType.Edit);

        Assert.That(accessA.AllowedClientIds, Is.EqualTo(new[] { ClientA }));
        Assert.That(accessB.AllowedClientIds, Is.EqualTo(new[] { ClientB }),
            "o segundo usuário recebeu o escopo do primeiro — vazamento de cache entre usuários");
    }

    [Test]
    public async Task Each_user_scope_is_cached_independently()
    {
        var permissions = new PerUserPermissionService();
        var context = new ScopeContext(permissions);

        context.SetUserId(UserA);
        await context.GetAccessAsync(ResourceType.Users, ActionType.Edit);
        await context.GetAccessAsync(ResourceType.Users, ActionType.Edit); // deve vir do cache

        Assert.That(permissions.Calls, Is.EqualTo(1), "a segunda chamada do MESMO usuário deveria usar o cache");

        context.SetUserId(UserB);
        await context.GetAccessAsync(ResourceType.Users, ActionType.Edit);
        await context.GetAccessAsync(ResourceType.Users, ActionType.Edit);

        Assert.That(permissions.Calls, Is.EqualTo(2), "cada usuário deve ter sua própria entrada, e só uma por par");
    }

    [Test]
    public async Task Global_access_is_scoped_per_user_too()
    {
        var permissions = new PerUserPermissionService();
        var context = new ScopeContext(permissions);

        context.SetUserId(UserA);
        var hasA = await context.HasGlobalAccessAsync(ResourceType.Agents, ActionType.View);

        context.SetUserId(UserB);
        var hasB = await context.HasGlobalAccessAsync(ResourceType.Agents, ActionType.View);

        Assert.That(hasA, Is.False);
        Assert.That(hasB, Is.False);
    }

    [Test]
    public async Task Without_a_user_it_denies_and_never_caches()
    {
        var permissions = new PerUserPermissionService();
        var context = new ScopeContext(permissions);

        var access = await context.GetAccessAsync(ResourceType.Users, ActionType.Edit);

        Assert.That(access.HasGlobalAccess, Is.False);
        Assert.That(access.AllowedClientIds, Is.Empty);
        Assert.That(permissions.Calls, Is.EqualTo(0), "sem usuário definido não se consulta nem cacheia");
    }
}
