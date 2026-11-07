using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Row-level security da listagem de usuários (Fase B1 da auditoria de identidade,
/// 2026-11-07).
///
/// Antes, GET /api/v1/users exigia apenas Users.View em QUALQUER escopo e devolvia
/// TODOS os usuários da plataforma. Com um administrador escopado a cliente isso
/// significava: um cliente enxergando a lista de usuários de todos os outros.
///
/// A regra implementada: um usuário é visível quando possui ao menos uma atribuição
/// de papel cujo escopo está contido no escopo do administrador. Sem escopo
/// concedido, NADA é visível (deny-by-default).
/// </summary>
[TestFixture]
public class UserScopedListingTests
{
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SiteA1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SiteB1 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    /// <summary>
    /// Cria dois usuários por cliente (+ um global) e devolve o repositório.
    /// Layout: userA (grupo com papel escopado em ClientA), userB (ClientB),
    /// userGlobal (grupo com papel Global).
    /// </summary>
    private static async Task<(UserRepository Repo, Guid UserA, Guid UserB, Guid UserGlobal)> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("user-scope-" + Guid.NewGuid().ToString("N"))
            .Options;
        var db = new IdentityTestDbContext(options);

        var role = new Role { Id = Guid.NewGuid(), Name = "Support" };
        db.Roles.Add(role);

        var userA = new User { Id = Guid.NewGuid(), Login = "ana.cA", Email = "a@x.com", FullName = "A" };
        var userB = new User { Id = Guid.NewGuid(), Login = "bruno.cB", Email = "b@x.com", FullName = "B" };
        var userGlobal = new User { Id = Guid.NewGuid(), Login = "root", Email = "r@x.com", FullName = "R" };
        db.Users.AddRange(userA, userB, userGlobal);

        var groupA = new UserGroup { Id = Guid.NewGuid(), Name = "Suporte A", IsActive = true };
        var groupB = new UserGroup { Id = Guid.NewGuid(), Name = "Suporte B", IsActive = true };
        var groupGlobal = new UserGroup { Id = Guid.NewGuid(), Name = "Plataforma", IsActive = true };
        db.UserGroups.AddRange(groupA, groupB, groupGlobal);

        db.UserGroupMemberships.AddRange(
            new UserGroupMembership { UserId = userA.Id, GroupId = groupA.Id },
            new UserGroupMembership { UserId = userB.Id, GroupId = groupB.Id },
            new UserGroupMembership { UserId = userGlobal.Id, GroupId = groupGlobal.Id });

        db.UserGroupRoles.AddRange(
            new UserGroupRole { Id = Guid.NewGuid(), GroupId = groupA.Id, RoleId = role.Id, ScopeLevel = ScopeLevel.Client, ScopeId = ClientA },
            new UserGroupRole { Id = Guid.NewGuid(), GroupId = groupB.Id, RoleId = role.Id, ScopeLevel = ScopeLevel.Client, ScopeId = ClientB },
            new UserGroupRole { Id = Guid.NewGuid(), GroupId = groupGlobal.Id, RoleId = role.Id, ScopeLevel = ScopeLevel.Global, ScopeId = null });

        await db.SaveChangesAsync();
        return (new UserRepository(db), userA.Id, userB.Id, userGlobal.Id);
    }

    [Test]
    public async Task Global_admin_sees_every_user()
    {
        var (repo, userA, userB, userGlobal) = await SeedAsync();

        var page = await repo.GetScopedPageAsync(true, [], [], null);
        var ids = page.Select(u => u.Id).ToHashSet();

        Assert.That(ids, Does.Contain(userA));
        Assert.That(ids, Does.Contain(userB));
        Assert.That(ids, Does.Contain(userGlobal));
    }

    [Test]
    public async Task Client_admin_sees_only_users_assigned_inside_its_client()
    {
        var (repo, userA, userB, userGlobal) = await SeedAsync();

        var page = await repo.GetScopedPageAsync(false, [ClientA], [], null);
        var ids = page.Select(u => u.Id).ToHashSet();

        Assert.That(ids, Does.Contain(userA));
        Assert.That(ids, Does.Not.Contain(userB), "usuário de outro cliente não pode ser listado");
        Assert.That(ids, Does.Not.Contain(userGlobal), "usuário global não pertence ao escopo do cliente");
    }

    [Test]
    public async Task Site_scoped_admin_sees_only_users_of_that_site()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("user-scope-site-" + Guid.NewGuid().ToString("N")).Options;
        var db = new IdentityTestDbContext(options);

        var role = new Role { Id = Guid.NewGuid(), Name = "Remote" };
        db.Roles.Add(role);

        var atSiteUser = new User { Id = Guid.NewGuid(), Login = "sitio1", Email = "s1@x.com", FullName = "S1" };
        var otherSiteUser = new User { Id = Guid.NewGuid(), Login = "sitio9", Email = "s9@x.com", FullName = "S9" };
        db.Users.AddRange(atSiteUser, otherSiteUser);

        var groupAt = new UserGroup { Id = Guid.NewGuid(), Name = "G1", IsActive = true };
        var groupOther = new UserGroup { Id = Guid.NewGuid(), Name = "G9", IsActive = true };
        db.UserGroups.AddRange(groupAt, groupOther);
        db.UserGroupMemberships.AddRange(
            new UserGroupMembership { UserId = atSiteUser.Id, GroupId = groupAt.Id },
            new UserGroupMembership { UserId = otherSiteUser.Id, GroupId = groupOther.Id });
        db.UserGroupRoles.AddRange(
            new UserGroupRole { Id = Guid.NewGuid(), GroupId = groupAt.Id, RoleId = role.Id, ScopeLevel = ScopeLevel.Site, ScopeId = SiteA1 },
            new UserGroupRole { Id = Guid.NewGuid(), GroupId = groupOther.Id, RoleId = role.Id, ScopeLevel = ScopeLevel.Site, ScopeId = SiteB1 });
        await db.SaveChangesAsync();

        var repo = new UserRepository(db);
        var page = await repo.GetScopedPageAsync(false, [], [SiteA1], null);
        var ids = page.Select(u => u.Id).ToHashSet();

        Assert.That(ids, Does.Contain(atSiteUser.Id));
        Assert.That(ids, Does.Not.Contain(otherSiteUser.Id));
    }

    [Test]
    public async Task Admin_without_any_scope_sees_nothing()
    {
        // Deny-by-default: sem escopo concedido não se vaza a base inteira.
        var (repo, _, _, _) = await SeedAsync();

        var page = await repo.GetScopedPageAsync(false, [], [], null);
        Assert.That(page.ToList(), Is.Empty);
    }

    [Test]
    public async Task Scoped_count_matches_the_scoped_page()
    {
        var (repo, _, _, _) = await SeedAsync();

        var count = await repo.CountScopedAsync(false, [ClientA], []);
        Assert.That(count, Is.EqualTo(1));

        var globalCount = await repo.CountScopedAsync(true, [], []);
        Assert.That(globalCount, Is.EqualTo(3));
    }

    /// <summary>Contexto reduzido às entidades de identidade exercitadas aqui.</summary>
    private sealed class IdentityTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(User), typeof(Role), typeof(Permission), typeof(RolePermission),
                typeof(UserGroup), typeof(UserGroupMembership), typeof(UserGroupRole)
            };

            foreach (var entityType in typeof(User).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<User>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Role>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Permission>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<RolePermission>(entity => entity.HasKey(item => new { item.RoleId, item.PermissionId }));
            modelBuilder.Entity<UserGroup>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<UserGroupMembership>(entity => entity.HasKey(item => new { item.UserId, item.GroupId }));
            modelBuilder.Entity<UserGroupRole>(entity => entity.HasKey(item => item.Id));
        }
    }
}
