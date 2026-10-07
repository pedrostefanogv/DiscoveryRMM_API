using Discovery.Core.Entities.Identity;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// A consulta de permissões passou a filtrar grupos e roles inativos
/// (UserGroupRepository.GetRolesWithPermissionsForUserAsync). O provider InMemory
/// não valida tradução de SQL, então este teste compila a mesma expressão contra o
/// provider Npgsql (sem abrir conexão) para garantir que o EF consegue traduzi-la.
/// </summary>
[TestFixture]
public class IdentityPermissionQueryTranslationTests
{
    [Test]
    public void RolesWithPermissionsQuery_TranslatesToPostgresSql()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=discovery_translation_only;Username=none;Password=none",
                npgsql => npgsql.UseVector())
            .Options;

        using var db = new DiscoveryDbContext(options);

        var userId = Guid.NewGuid();
        var activeGroupIds = db.UserGroups.Where(g => g.IsActive).Select(g => g.Id);

        var query = db.UserGroupMemberships
            .Where(m => m.UserId == userId && activeGroupIds.Contains(m.GroupId))
            .Join(db.UserGroupRoles, m => m.GroupId, r => r.GroupId, (m, r) => r)
            .Join(db.Roles, r => r.RoleId, role => role.Id, (r, role) => new { Assignment = r, Role = role })
            .Where(x => x.Role.IsActive)
            .Join(db.RolePermissions, x => x.Assignment.RoleId, rp => rp.RoleId, (x, rp) => new { x.Assignment, PermissionId = rp.PermissionId })
            .Join(db.Permissions, x => x.PermissionId, p => p.Id, (x, p) => new { x.Assignment, Permission = p });

        var sql = query.ToQueryString();

        Assert.That(sql, Does.Contain("user_group_memberships"));
        Assert.That(sql, Does.Contain("user_groups"));
        Assert.That(sql, Does.Contain("user_group_roles"));
        Assert.That(sql, Does.Contain("role_permissions"));
        Assert.That(sql, Does.Contain("is_active"));
    }

    [Test]
    public void InactiveRoleFilter_IsPartOfTheGeneratedSql()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=discovery_translation_only;Username=none;Password=none",
                npgsql => npgsql.UseVector())
            .Options;

        using var db = new DiscoveryDbContext(options);

        var activeGroups = db.UserGroups.Where(g => g.IsActive).Select(g => g.Id);
        var sql = db.UserGroupMemberships
            .Where(m => activeGroups.Contains(m.GroupId))
            .Join(db.UserGroupRoles, m => m.GroupId, r => r.GroupId, (m, r) => r)
            .Join(db.Roles, r => r.RoleId, role => role.Id, (r, role) => role)
            .Where(role => role.IsActive)
            .ToQueryString();

        // Duas ocorrências: o filtro do grupo (subquery) e o da role (join).
        Assert.That(sql.Split("is_active").Length - 1, Is.GreaterThanOrEqualTo(2));
    }
}
