using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.UserGroups.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Cqrs.UserGroups;
using Discovery.Infrastructure.Services.Identity;

namespace Discovery.Tests;

/// <summary>
/// Regressão do achado P0-2 da auditoria de identidade (2026-11-07): a atribuição
/// de papel a um grupo não verificava QUEM estava concedendo.
///
/// O payload mais barato de escalação era { roleId: &lt;papel admin&gt;, scopeLevel: "Global" },
/// aceito de qualquer portador de Users:Edit — inclusive de um administrador
/// escopado a um único cliente, que assim escapava do próprio tenant.
/// </summary>
[TestFixture]
public class RoleGrantScopePolicyTests
{
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SiteA1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SiteB1 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private static UserScopeAccess Access(bool global = false, Guid[]? clients = null, Guid[]? sites = null) => new()
    {
        HasGlobalAccess = global,
        AllowedClientIds = clients ?? [],
        AllowedSiteIds = sites ?? []
    };

    [Test]
    public void Global_granter_can_grant_anything()
    {
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(global: true), ScopeLevel.Global, null), Is.True);
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(global: true), ScopeLevel.Client, ClientB), Is.True);
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(global: true), ScopeLevel.Site, SiteB1), Is.True);
    }

    [Test]
    public void Client_scoped_granter_cannot_grant_global_scope()
    {
        // O caso central da escalação: admin do cliente A se promovendo a global.
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(clients: [ClientA]), ScopeLevel.Global, null), Is.False);
    }

    [Test]
    public void Client_scoped_granter_can_grant_its_own_client_only()
    {
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(clients: [ClientA]), ScopeLevel.Client, ClientA), Is.True);
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(clients: [ClientA]), ScopeLevel.Client, ClientB), Is.False);
    }

    [Test]
    public void Site_scoped_granter_can_grant_only_its_own_site()
    {
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(sites: [SiteA1]), ScopeLevel.Site, SiteA1), Is.True);
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(sites: [SiteA1]), ScopeLevel.Site, SiteB1), Is.False);
    }

    [Test]
    public void Client_granter_can_grant_a_site_of_its_own_client()
    {
        // Uma atribuição de Client abrange todos os sites daquele cliente, então
        // conceder num site do próprio cliente não amplia o próprio poder.
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(clients: [ClientA]), ScopeLevel.Site, SiteA1, ClientA), Is.True);
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(clients: [ClientA]), ScopeLevel.Site, SiteB1, ClientB), Is.False);
    }

    [Test]
    public void Empty_or_null_scope_is_never_grantable_by_a_scoped_user()
    {
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(clients: [ClientA]), ScopeLevel.Client, null), Is.False);
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(clients: [ClientA]), ScopeLevel.Client, Guid.Empty), Is.False);
        Assert.That(RoleGrantScopePolicy.CanGrant(Access(sites: [SiteA1]), ScopeLevel.Site, null), Is.False);
    }

    [Test]
    public void Client_grant_covers_a_site_of_that_client()
    {
        // Herança documentada da policy: uma atribuição de Client abrange todos os
        // sites daquele cliente. Sem passar o cliente pai do site, um admin de
        // cliente era WRONGLY bloqueado de administrar grupos cuja atribuição é
        // Site-scoped (bug #9 da revisão final).
        Assert.That(
            RoleGrantScopePolicy.CanGrant(Access(clients: [ClientA]), ScopeLevel.Site, SiteA1, parentClientId: ClientA),
            Is.True);
    }

    [Test]
    public void Client_grant_does_not_cover_a_site_of_another_client()
    {
        Assert.That(
            RoleGrantScopePolicy.CanGrant(Access(clients: [ClientA]), ScopeLevel.Site, SiteB1, parentClientId: ClientB),
            Is.False);
    }

    [Test]
    public void Site_grant_of_the_same_site_works_without_the_parent_client()
    {
        // Caminho direto: o ator tem o site explicitamente, não precisa do pai.
        Assert.That(
            RoleGrantScopePolicy.CanGrant(Access(sites: [SiteA1]), ScopeLevel.Site, SiteA1, parentClientId: null),
            Is.True);
    }

    [Test]
    public void Missing_parent_client_still_denies_inherited_site_grant()
    {
        // Documenta o defeito corrigido: SEM o cliente pai a herança não se aplica —
        // é exatamente por isso que os call sites precisam resolver o site.
        Assert.That(
            RoleGrantScopePolicy.CanGrant(Access(clients: [ClientA]), ScopeLevel.Site, SiteA1, parentClientId: null),
            Is.False);
    }

    [Test]
    public void Null_access_cannot_grant_anything()
    {
        Assert.That(RoleGrantScopePolicy.CanGrant(null, ScopeLevel.Client, ClientA), Is.False);
        Assert.That(RoleGrantScopePolicy.CanGrant(null, ScopeLevel.Global, null), Is.False);
    }
}

/// <summary>
/// Cobre a guarda dentro do handler, provando que a escalação é recusada de fato
/// (não apenas na policy).
/// </summary>
[TestFixture]
public class AssignGroupRoleGrantGuardTests
{
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Actor = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static UserScopeAccess ScopedTo(params Guid[] clients) => new()
    {
        HasGlobalAccess = false,
        AllowedClientIds = clients,
        AllowedSiteIds = []
    };

    [Test]
    public async Task Client_admin_cannot_grant_global_scope()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Admin", IsSystem = true };
        var handler = Build(role, ScopedTo(ClientA));

        var result = await handler.Handle(
            new AssignGroupRoleCommand(Guid.NewGuid(), role.Id, ScopeLevel.Global, null, Actor),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Forbidden"));
        Assert.That(result.Errors[0].Message, Does.Contain("escopo"));
    }

    [Test]
    public async Task Client_admin_cannot_grant_scope_of_another_client()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Support" };
        var handler = Build(role, ScopedTo(ClientA), new Dictionary<Guid, Client>
        {
            [ClientB] = new Client { Id = ClientB }
        });

        var result = await handler.Handle(
            new AssignGroupRoleCommand(Guid.NewGuid(), role.Id, ScopeLevel.Client, ClientB, Actor),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Forbidden"));
    }

    [Test]
    public async Task Client_admin_can_grant_its_own_client()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Support" };
        var handler = Build(role, ScopedTo(ClientA), new Dictionary<Guid, Client>
        {
            [ClientA] = new Client { Id = ClientA }
        });

        var result = await handler.Handle(
            new AssignGroupRoleCommand(Guid.NewGuid(), role.Id, ScopeLevel.Client, ClientA, Actor),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task Global_admin_can_grant_global_scope()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Admin", IsSystem = true };
        var access = new UserScopeAccess { HasGlobalAccess = true };
        var handler = Build(role, access);

        var result = await handler.Handle(
            new AssignGroupRoleCommand(Guid.NewGuid(), role.Id, ScopeLevel.Global, null, Actor),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task Non_existent_scope_id_is_rejected()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = "Support" };
        var access = new UserScopeAccess { HasGlobalAccess = true };
        // Global pode conceder, mas o cliente informado não existe → validação.
        var handler = Build(role, access);

        var result = await handler.Handle(
            new AssignGroupRoleCommand(Guid.NewGuid(), role.Id, ScopeLevel.Client, ClientB, Actor),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    private static AssignGroupRoleCommandHandler Build(
        Role role, UserScopeAccess access,
        Dictionary<Guid, Client>? clients = null)
    {
        var groups = new GroupRepoStub();
        var roles = new RoleRepoStub(role);
        var clientRepo = new ClientRepoStub(clients ?? []);
        return new AssignGroupRoleCommandHandler(
            groups, roles, new NoopPermissionService(), new ScopeStub(access),
            clientRepo, new SiteRepoStub());
    }

    private sealed class GroupRepoStub : IUserGroupRepository
    {
        public List<UserGroupRole> Assignments { get; } = [];
        public Task<UserGroup?> GetByIdAsync(Guid id) => Task.FromResult<UserGroup?>(new UserGroup { Id = id, IsActive = true });
        public Task<IEnumerable<UserGroup>> GetAllAsync() => Task.FromResult<IEnumerable<UserGroup>>([]);
        public Task<UserGroup> CreateAsync(UserGroup group) => Task.FromResult(group);
        public Task<UserGroup> UpdateAsync(UserGroup group) => Task.FromResult(group);
        public Task<bool> DeleteAsync(Guid id) => Task.FromResult(true);
        public Task AddMemberAsync(Guid groupId, Guid userId) => Task.CompletedTask;
        public Task RemoveMemberAsync(Guid groupId, Guid userId) => Task.CompletedTask;
        public Task<IEnumerable<UserGroupMembership>> GetMembershipsAsync(Guid groupId)
            => Task.FromResult<IEnumerable<UserGroupMembership>>([]);
        public Task<UserGroupMembership?> GetMembershipAsync(Guid groupId, Guid userId) => Task.FromResult<UserGroupMembership?>(null);
        public Task<IEnumerable<Guid>> GetMemberIdsAsync(Guid groupId) => Task.FromResult<IEnumerable<Guid>>([]);
        public Task<IEnumerable<Guid>> GetGroupIdsForUserAsync(Guid userId) => Task.FromResult<IEnumerable<Guid>>([]);
        public Task<IEnumerable<UserGroupRole>> GetRolesForGroupAsync(Guid groupId)
            => Task.FromResult<IEnumerable<UserGroupRole>>(Assignments);
        public Task<IEnumerable<UserGroupRole>> GetRolesForUserAsync(Guid userId)
            => Task.FromResult<IEnumerable<UserGroupRole>>([]);
        public Task<IReadOnlyList<Discovery.Core.DTOs.Identity.RoleAssignmentWithPermissions>> GetRolesWithPermissionsForUserAsync(Guid userId)
            => Task.FromResult<IReadOnlyList<Discovery.Core.DTOs.Identity.RoleAssignmentWithPermissions>>([]);
        public Task AssignRoleAsync(UserGroupRole assignment)
        {
            assignment.Id = assignment.Id == Guid.Empty ? Guid.NewGuid() : assignment.Id;
            Assignments.Add(assignment);
            return Task.CompletedTask;
        }
        public Task RemoveRoleAssignmentAsync(Guid assignmentId)
        {
            Assignments.RemoveAll(a => a.Id == assignmentId);
            return Task.CompletedTask;
        }
    }

    private sealed class RoleRepoStub(Role role) : IRoleRepository
    {
        public Task<Role?> GetByIdAsync(Guid id) => Task.FromResult<Role?>(id == role.Id ? role : null);
        public Task<IEnumerable<Role>> GetByIdsAsync(IEnumerable<Guid> ids) => Task.FromResult<IEnumerable<Role>>([role]);
        public Task<IEnumerable<Role>> GetAllAsync() => Task.FromResult<IEnumerable<Role>>([role]);
        public Task<Role> CreateAsync(Role r) => Task.FromResult(r);
        public Task<Role> UpdateAsync(Role r) => Task.FromResult(r);
        public Task<bool> DeleteAsync(Guid id) => Task.FromResult(true);
        public Task<IEnumerable<Permission>> GetPermissionsForRoleAsync(Guid roleId) => Task.FromResult<IEnumerable<Permission>>([]);
        public Task AddPermissionToRoleAsync(Guid roleId, Guid permissionId) => Task.CompletedTask;
        public Task RemovePermissionFromRoleAsync(Guid roleId, Guid permissionId) => Task.CompletedTask;
        public Task<IEnumerable<Permission>> GetAllPermissionsAsync() => Task.FromResult<IEnumerable<Permission>>([]);
        public Task<Permission?> GetPermissionAsync(Guid id) => Task.FromResult<Permission?>(null);
    }

    private sealed class ClientRepoStub(Dictionary<Guid, Client> clients) : IClientRepository
    {
        public Task<Client?> GetByIdAsync(Guid id) => Task.FromResult(clients.GetValueOrDefault(id));
        public Task<IEnumerable<Client>> GetAllAsync(bool includeInactive = false) => Task.FromResult<IEnumerable<Client>>([]);
        public Task<Client> CreateAsync(Client client) => Task.FromResult(client);
        public Task UpdateAsync(Client client) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    private sealed class SiteRepoStub : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id) => Task.FromResult<Site?>(null);
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) => Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false) => Task.FromResult<IEnumerable<Site>>([]);
        public Task<Site> CreateAsync(Site site) => Task.FromResult(site);
        public Task UpdateAsync(Site site) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    private sealed class ScopeStub(UserScopeAccess access) : IScopeContext
    {
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
        public void SetUserId(Guid userId) { }
        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action) => Task.FromResult(access);
        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action) => Task.FromResult(access.HasGlobalAccess);
    }

    private sealed class NoopPermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, ResourceType resource, ActionType action,
            ScopeLevel scopeLevel = ScopeLevel.Global, Guid? scopeId = null, Guid? parentScopeId = null)
            => Task.FromResult(true);
        public Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess());
        public Task InvalidateUserCacheAsync(Guid userId) => Task.CompletedTask;
        public Task InvalidateAllCacheAsync() => Task.CompletedTask;
    }
}
