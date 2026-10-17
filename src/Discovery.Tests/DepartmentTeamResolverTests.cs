using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Resolvedor único da equipe efetiva (D7): próprios + herdados do global pai,
/// dedupe por usuário, global nunca herda e pai não global é ignorado.
/// </summary>
public class DepartmentTeamResolverTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("department-team-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new TeamTestDbContext(options);
    }

    private static User NewUser(bool active = true) => new()
    {
        Id = Guid.NewGuid(),
        Login = "u" + Guid.NewGuid().ToString("N")[..6],
        Email = "u@x.com",
        FullName = "Usuario",
        IsActive = active
    };

    private static Department NewDepartment(
        Guid? clientId, Guid? parentId = null, bool active = true)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "Depto",
            IsActive = active,
            InheritFromGlobalId = parentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static DepartmentMember NewMember(
        Guid departmentId, Guid userId, bool active = true, DateTime? createdAt = null)
        => new()
        {
            Id = Guid.NewGuid(),
            DepartmentId = departmentId,
            UserId = userId,
            IsActive = active,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };

    [Test]
    public async Task Departamento_vazio_herda_membros_do_global()
    {
        await using var db = CreateDb();
        var global = NewDepartment(clientId: null);
        var client = NewDepartment(clientId: Guid.NewGuid(), parentId: global.Id);
        var inheritedUser = NewUser();
        db.Departments.AddRange(global, client);
        db.Users.Add(inheritedUser);
        db.DepartmentMembers.Add(NewMember(global.Id, inheritedUser.Id));
        await db.SaveChangesAsync();

        var members = await new DepartmentTeamResolver(db).ResolveMembersAsync(client.Id);

        Assert.That(members.Select(m => m.UserId), Is.EquivalentTo(new[] { inheritedUser.Id }));
        Assert.That(members[0].Origin, Is.EqualTo($"inherited:{global.Id}"));
    }

    [Test]
    public async Task Com_proprios_a_equipe_e_a_uniao_sem_duplicar()
    {
        await using var db = CreateDb();
        var global = NewDepartment(clientId: null);
        var client = NewDepartment(clientId: Guid.NewGuid(), parentId: global.Id);
        var ownUser = NewUser();
        var inheritedUser = NewUser();
        var bothUser = NewUser();

        db.Departments.AddRange(global, client);
        db.Users.AddRange(ownUser, inheritedUser, bothUser);
        db.DepartmentMembers.AddRange(
            NewMember(client.Id, ownUser.Id, createdAt: DateTime.UtcNow.AddMinutes(-3)),
            NewMember(client.Id, bothUser.Id, createdAt: DateTime.UtcNow.AddMinutes(-2)),
            NewMember(global.Id, inheritedUser.Id, createdAt: DateTime.UtcNow.AddMinutes(-1)),
            NewMember(global.Id, bothUser.Id, createdAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var members = await new DepartmentTeamResolver(db).ResolveMembersAsync(client.Id);

        Assert.That(members.Select(m => m.UserId).Distinct().Count(), Is.EqualTo(3));
        Assert.That(members.Count, Is.EqualTo(3), "usuário presente nos dois departamentos não duplica");
        Assert.That(members[0].UserId, Is.EqualTo(ownUser.Id), "próprios primeiro");
        Assert.That(members[1].UserId, Is.EqualTo(bothUser.Id));
        Assert.That(members[1].Origin, Is.EqualTo("own"));
        Assert.That(members[2].UserId, Is.EqualTo(inheritedUser.Id));
    }

    [Test]
    public async Task Departamento_global_nunca_herda()
    {
        await using var db = CreateDb();
        var other = NewDepartment(clientId: null);
        var global = NewDepartment(clientId: null, parentId: other.Id);
        var ownUser = NewUser();
        var otherUser = NewUser();

        db.Departments.AddRange(other, global);
        db.Users.AddRange(ownUser, otherUser);
        db.DepartmentMembers.AddRange(
            NewMember(global.Id, ownUser.Id),
            NewMember(other.Id, otherUser.Id));
        await db.SaveChangesAsync();

        var members = await new DepartmentTeamResolver(db).ResolveMembersAsync(global.Id);

        Assert.That(members.Select(m => m.UserId), Is.EquivalentTo(new[] { ownUser.Id }));
        Assert.That(members[0].Origin, Is.EqualTo("own"));
    }

    [Test]
    public async Task Pai_nao_global_e_ignorado_na_leitura()
    {
        await using var db = CreateDb();
        var nonGlobalParent = NewDepartment(clientId: Guid.NewGuid());
        var client = NewDepartment(clientId: Guid.NewGuid(), parentId: nonGlobalParent.Id);
        var ownUser = NewUser();
        var parentUser = NewUser();

        db.Departments.AddRange(nonGlobalParent, client);
        db.Users.AddRange(ownUser, parentUser);
        db.DepartmentMembers.AddRange(
            NewMember(client.Id, ownUser.Id),
            NewMember(nonGlobalParent.Id, parentUser.Id));
        await db.SaveChangesAsync();

        var members = await new DepartmentTeamResolver(db).ResolveMembersAsync(client.Id);

        Assert.That(members.Select(m => m.UserId), Is.EquivalentTo(new[] { ownUser.Id }));
    }

    [Test]
    public async Task Filtra_departamento_e_usuario_inativos()
    {
        await using var db = CreateDb();
        var global = NewDepartment(clientId: null);
        var client = NewDepartment(clientId: Guid.NewGuid(), parentId: global.Id);
        var activeUser = NewUser();
        var inactiveUser = NewUser(active: false);

        db.Departments.AddRange(global, client);
        db.Users.AddRange(activeUser, inactiveUser);
        db.DepartmentMembers.AddRange(
            NewMember(client.Id, activeUser.Id),
            NewMember(client.Id, inactiveUser.Id),
            NewMember(global.Id, activeUser.Id));
        await db.SaveChangesAsync();

        var members = await new DepartmentTeamResolver(db).ResolveMembersAsync(client.Id);
        Assert.That(members.Select(m => m.UserId), Is.EquivalentTo(new[] { activeUser.Id }));

        client.IsActive = false;
        await db.SaveChangesAsync();
        var inactiveMembers = await new DepartmentTeamResolver(db).ResolveMembersAsync(client.Id);
        Assert.That(inactiveMembers, Is.Empty, "departamento inativo não tem equipe");
    }

    private sealed class TeamTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(User), typeof(Department), typeof(DepartmentMember)
            };

            foreach (var entityType in typeof(Department).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<User>(e => e.HasKey(u => u.Id));
            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<DepartmentMember>(e => e.HasKey(m => m.Id));
        }
    }
}
