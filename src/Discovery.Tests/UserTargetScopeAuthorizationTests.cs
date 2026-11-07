using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Regressão do bug #2 encontrado na revisão pós-implementação (2026-11-07).
///
/// Ao trocar os endpoints de identidade para <c>ScopeSource.AccessList</c> (para
/// permitir administrador escopado a cliente), eu NÃO validei o escopo do ALVO.
/// <c>AccessList</c> apenas prova que o ator tem a permissão em algum lugar; ela
/// não diz nada sobre o id solicitado.
///
/// Resultado antes da correção: um administrador do cliente A podia
/// GET/PUT/DELETE de qualquer usuário da plataforma, resetar a senha de um
/// administrador global e revogar o MFA dele — bastando conhecer o id. Era um
/// buraco entre tenants PIOR que o original, por ser administrativo.
///
/// A correção (IsUserInScopeAsync) reusa o MESMO predicado da listagem: o que o
/// ator não enxerga na lista, também não pode operar por id.
/// </summary>
[TestFixture]
public class UserTargetScopeAuthorizationTests
{
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static async Task<(UserRepository Scoped, Guid UserA, Guid UserB, Guid UserGlobal)> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("user-target-scope-" + Guid.NewGuid().ToString("N"))
            .Options;
        var db = new TargetScopeTestDbContext(options);

        var role = new Role { Id = Guid.NewGuid(), Name = "Support" };
        db.Roles.Add(role);

        var userA = new User { Id = Guid.NewGuid(), Login = "a", Email = "a@x.com", FullName = "A" };
        var userB = new User { Id = Guid.NewGuid(), Login = "b", Email = "b@x.com", FullName = "B" };
        var userGlobal = new User { Id = Guid.NewGuid(), Login = "root", Email = "r@x.com", FullName = "R" };
        db.Users.AddRange(userA, userB, userGlobal);

        var groupA = new UserGroup { Id = Guid.NewGuid(), Name = "GA", IsActive = true };
        var groupB = new UserGroup { Id = Guid.NewGuid(), Name = "GB", IsActive = true };
        var groupG = new UserGroup { Id = Guid.NewGuid(), Name = "GG", IsActive = true };
        db.UserGroups.AddRange(groupA, groupB, groupG);

        db.UserGroupMemberships.AddRange(
            new UserGroupMembership { UserId = userA.Id, GroupId = groupA.Id },
            new UserGroupMembership { UserId = userB.Id, GroupId = groupB.Id },
            new UserGroupMembership { UserId = userGlobal.Id, GroupId = groupG.Id });

        db.UserGroupRoles.AddRange(
            new UserGroupRole { Id = Guid.NewGuid(), GroupId = groupA.Id, RoleId = role.Id, ScopeLevel = ScopeLevel.Client, ScopeId = ClientA },
            new UserGroupRole { Id = Guid.NewGuid(), GroupId = groupB.Id, RoleId = role.Id, ScopeLevel = ScopeLevel.Client, ScopeId = ClientB },
            new UserGroupRole { Id = Guid.NewGuid(), GroupId = groupG.Id, RoleId = role.Id, ScopeLevel = ScopeLevel.Global, ScopeId = null });

        await db.SaveChangesAsync();
        return (new UserRepository(db), userA.Id, userB.Id, userGlobal.Id);
    }

    [Test]
    public async Task Client_admin_cannot_target_a_user_of_another_client()
    {
        var (repo, userA, userB, _) = await SeedAsync();

        Assert.That(await repo.IsUserInScopeAsync(false, [ClientA], [], userA), Is.True);
        Assert.That(await repo.IsUserInScopeAsync(false, [ClientA], [], userB), Is.False,
            "admin do cliente A não pode operar um usuário do cliente B");
    }

    [Test]
    public async Task Client_admin_cannot_target_a_global_administrator()
    {
        // O caso mais grave: resetar a senha / revogar o MFA de um admin global.
        var (repo, _, _, userGlobal) = await SeedAsync();

        Assert.That(await repo.IsUserInScopeAsync(false, [ClientA], [], userGlobal), Is.False,
            "um usuário global não pertence ao escopo do cliente");
    }

    [Test]
    public async Task Global_admin_can_target_anyone()
    {
        var (repo, userA, userB, userGlobal) = await SeedAsync();

        Assert.That(await repo.IsUserInScopeAsync(true, [], [], userA), Is.True);
        Assert.That(await repo.IsUserInScopeAsync(true, [], [], userB), Is.True);
        Assert.That(await repo.IsUserInScopeAsync(true, [], [], userGlobal), Is.True);
    }

    [Test]
    public async Task Actor_without_scope_cannot_target_anyone()
    {
        var (repo, userA, _, _) = await SeedAsync();

        Assert.That(await repo.IsUserInScopeAsync(false, [], [], userA), Is.False,
            "deny-by-default: sem escopo concedido, nenhum alvo é autorizado");
    }

    [Test]
    public async Task Target_scope_matches_listing_visibility()
    {
        // Consistência: o que aparece na listagem é exatamente o que é administrável.
        var (repo, userA, userB, userGlobal) = await SeedAsync();

        var listed = (await repo.GetScopedPageAsync(false, [ClientA], [], null)).Select(u => u.Id).ToHashSet();

        Assert.That(await repo.IsUserInScopeAsync(false, [ClientA], [], userA), Is.EqualTo(listed.Contains(userA)));
        Assert.That(await repo.IsUserInScopeAsync(false, [ClientA], [], userB), Is.EqualTo(listed.Contains(userB)));
        Assert.That(await repo.IsUserInScopeAsync(false, [ClientA], [], userGlobal), Is.EqualTo(listed.Contains(userGlobal)));
    }

    private sealed class TargetScopeTestDbContext(DbContextOptions<DiscoveryDbContext> options)
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
