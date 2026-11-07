using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Roles.Commands;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Cqrs.Roles;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Regressão do achado P0-3 da auditoria de identidade (2026-11-07).
///
/// UpdateRole e DeleteRole já recusavam papéis de sistema (IsSystem), mas
/// AddPermission/RemovePermission NÃO checavam — era possível ampliar ou reduzir
/// silenciosamente o papel Admin embutido (ex.: conceder Users:Delete, remover
/// Users:Edit). A invariante agora vive no handler E no repositório.
/// </summary>
[TestFixture]
public class SystemRoleProtectionTests
{
    private const string SystemRoleName = "Admin";

    private static (RoleRepository Repo, DiscoveryDbContext Db, SqliteConnection Connection) NewRepo()
    {
        // SQLite (e não InMemory): RemovePermissionFromRoleAsync usa ExecuteDeleteAsync,
        // que o provider InMemory não implementa. O projeto já usa SQLite justamente
        // para cobrir caminhos relacionais.
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new RoleIdentityTestDbContext(options);
        db.Database.EnsureCreated();
        return (new RoleRepository(db), db, connection);
    }

    private static async Task<(Guid RoleId, Guid PermissionId)> SeedAsync(
        DiscoveryDbContext db, bool isSystem)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = SystemRoleName,
            Type = isSystem ? RoleType.System : RoleType.Custom,
            IsSystem = isSystem
        };
        db.Roles.Add(role);

        var permission = new Permission
        {
            Id = Guid.NewGuid(),
            ResourceType = ResourceType.Users,
            ActionType = ActionType.Delete
        };
        db.Permissions.Add(permission);

        await db.SaveChangesAsync();
        return (role.Id, permission.Id);
    }

    [Test]
    public async Task Repository_refuses_to_add_permission_to_a_system_role()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;
        var (roleId, permissionId) = await SeedAsync(db, isSystem: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.AddPermissionToRoleAsync(roleId, permissionId));

        Assert.That((await repo.GetPermissionsForRoleAsync(roleId)).ToList(), Is.Empty);
    }

    [Test]
    public async Task Repository_refuses_to_remove_permission_from_a_system_role()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;
        var (roleId, permissionId) = await SeedAsync(db, isSystem: false);

        // Papel NÃO-sistema: concede e depois tenta remover num papel de sistema.
        await repo.AddPermissionToRoleAsync(roleId, permissionId);
        Assert.That((await repo.GetPermissionsForRoleAsync(roleId)).ToList(), Has.Count.EqualTo(1));

        var (systemRoleId, _) = await SeedAsync(db, isSystem: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.RemovePermissionFromRoleAsync(systemRoleId, permissionId));
    }

    [Test]
    public async Task Repository_allows_permission_changes_on_custom_roles()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;
        var (roleId, permissionId) = await SeedAsync(db, isSystem: false);

        await repo.AddPermissionToRoleAsync(roleId, permissionId);
        Assert.That((await repo.GetPermissionsForRoleAsync(roleId)).ToList(), Has.Count.EqualTo(1));

        await repo.RemovePermissionFromRoleAsync(roleId, permissionId);
        Assert.That((await repo.GetPermissionsForRoleAsync(roleId)).ToList(), Is.Empty);
    }

    [Test]
    public async Task Handler_rejects_add_permission_on_system_role_with_validation_error()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;
        var (roleId, permissionId) = await SeedAsync(db, isSystem: true);

        var handler = new AddRolePermissionCommandHandler(new RoleServiceStub(repo), new NoopPermissionService());
        var result = await handler.Handle(new AddRolePermissionCommand(roleId, permissionId), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task Handler_rejects_remove_permission_on_system_role_with_validation_error()
    {
        var (repo, db, connection) = NewRepo();
        using var _ = connection;
        var (roleId, permissionId) = await SeedAsync(db, isSystem: false);
        await repo.AddPermissionToRoleAsync(roleId, permissionId);

        var (systemRoleId, _) = await SeedAsync(db, isSystem: true);

        var handler = new RemoveRolePermissionCommandHandler(new RoleServiceStub(repo), new NoopPermissionService());
        var result = await handler.Handle(
            new RemoveRolePermissionCommand(systemRoleId, permissionId), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    /// <summary>RoleService real sobre o repositório real (sem reimplementar a regra).</summary>
    private sealed class RoleServiceStub(IRoleRepository repo) : IRoleService
    {
        public Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default)
            => repo.GetAllAsync().ContinueWith(t => (IReadOnlyList<Role>)t.Result.ToList());
        public Task<Role?> GetByIdAsync(Guid id, CancellationToken ct = default) => repo.GetByIdAsync(id);
        public Task<Role> CreateAsync(Role role, CancellationToken ct = default) => repo.CreateAsync(role);
        public Task<Role> UpdateAsync(Role role, CancellationToken ct = default) => repo.UpdateAsync(role);
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => repo.DeleteAsync(id);
        public Task<IEnumerable<Permission>> GetPermissionsAsync(Guid roleId, CancellationToken ct = default)
            => repo.GetPermissionsForRoleAsync(roleId);
        public Task AddPermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default)
            => repo.AddPermissionToRoleAsync(roleId, permissionId);
        public Task RemovePermissionAsync(Guid roleId, Guid permissionId, CancellationToken ct = default)
            => repo.RemovePermissionFromRoleAsync(roleId, permissionId);
        public Task<IEnumerable<Permission>> GetAllPermissionsAsync(CancellationToken ct = default)
            => repo.GetAllPermissionsAsync();
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

    /// <summary>Contexto reduzido às entidades de identidade usadas aqui.</summary>
    private sealed class RoleIdentityTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Role), typeof(Permission), typeof(RolePermission) };

            foreach (var entityType in typeof(Role).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Role>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Permission>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<RolePermission>(entity =>
            {
                entity.HasKey(item => new { item.RoleId, item.PermissionId });
            });
        }
    }
}
