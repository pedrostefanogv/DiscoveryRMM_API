using System.Text.Json;
using Discovery.Api.Controllers;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Support.Assignments;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Support;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// B2 — a tela de triagem usa a EQUIPE EFETIVA: lista membros próprios e
/// herdados do global pai e rejeita (422) edição de perfil de membro herdado.
/// </summary>
public class DepartmentAssignmentTeamMetricsTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("department-metrics-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new MetricsTestDbContext(options);
    }

    private static User NewUser(bool active = true) => new()
    {
        Id = Guid.NewGuid(),
        Login = "u" + Guid.NewGuid().ToString("N")[..6],
        Email = "u@x.com",
        FullName = "Usuario",
        IsActive = active
    };

    private static Department NewDepartment(Guid? clientId, Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Name = clientId is null ? "Global" : "Cliente",
        IsActive = true,
        InheritFromGlobalId = parentId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static DepartmentMember NewMember(Guid departmentId, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        DepartmentId = departmentId,
        UserId = userId,
        IsActive = true,
        SkillLevel = 1,
        Weight = 1m,
        AcceptsAiAssignment = true,
        CreatedAt = DateTime.UtcNow
    };

    private static async Task<(DiscoveryDbContext Db, Department Global, Department Client,
        User Own, User Inherited)> SeedAsync()
    {
        var db = CreateDb();
        var global = NewDepartment(clientId: null);
        var client = NewDepartment(clientId: Guid.NewGuid(), parentId: global.Id);
        var own = NewUser();
        var inherited = NewUser();

        db.Departments.AddRange(global, client);
        db.Users.AddRange(own, inherited);
        db.DepartmentMembers.AddRange(
            NewMember(client.Id, own.Id),
            NewMember(global.Id, inherited.Id));
        await db.SaveChangesAsync();

        return (db, global, client, own, inherited);
    }

    [Test]
    public async Task Team_metrics_lista_membro_herdado_do_global()
    {
        var (db, global, client, own, inherited) = await SeedAsync();
        await using var _db = db;

        var handler = new GetDepartmentAssignmentTeamMetricsQueryHandler(
            db, new FakeMetrics(), new DepartmentTeamResolver(db));

        var result = await handler.Handle(
            new GetDepartmentAssignmentTeamMetricsQuery(client.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var profiles = result.Value!;
        Assert.That(profiles.Select(p => p.UserId),
            Is.EquivalentTo(new[] { own.Id, inherited.Id }));

        var inheritedProfile = profiles.Single(p => p.UserId == inherited.Id);
        Assert.That(inheritedProfile.Origin, Is.EqualTo($"inherited:{global.Id}"));
        Assert.That(inheritedProfile.DepartmentId, Is.EqualTo(client.Id));
        Assert.That(inheritedProfile.Metrics, Is.Not.Null);

        Assert.That(profiles.Single(p => p.UserId == own.Id).Origin, Is.EqualTo("own"));
    }

    [Test]
    public async Task Put_perfil_de_membro_herdado_falha_sem_alterar_o_global()
    {
        var (db, global, client, _, inherited) = await SeedAsync();
        await using var _db = db;

        var handler = new UpdateDepartmentMemberProfileCommandHandler(
            db, new DepartmentTeamResolver(db));

        var result = await handler.Handle(new UpdateDepartmentMemberProfileCommand(
            client.Id, inherited.Id, SkillLevel: 5, Weight: 2m), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(result.Errors[0].Message, Does.Contain("gerenciado no departamento global"));

        // Nada foi alterado no vínculo global nem criado no departamento filho.
        var globalMember = await db.DepartmentMembers.AsNoTracking().SingleAsync(
            m => m.DepartmentId == global.Id && m.UserId == inherited.Id);
        Assert.That(globalMember.SkillLevel, Is.EqualTo(1));
        Assert.That(globalMember.Weight, Is.EqualTo(1m));
        Assert.That(await db.DepartmentMembers.AsNoTracking()
            .AnyAsync(m => m.DepartmentId == client.Id && m.UserId == inherited.Id), Is.False);
    }

    [Test]
    public async Task Put_perfil_de_membro_proprio_continua_funcionando()
    {
        var (db, _, client, own, _) = await SeedAsync();
        await using var _db = db;

        var handler = new UpdateDepartmentMemberProfileCommandHandler(
            db, new DepartmentTeamResolver(db));

        var result = await handler.Handle(new UpdateDepartmentMemberProfileCommand(
            client.Id, own.Id, SkillLevel: 5, Weight: 2m), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Origin, Is.EqualTo("own"));

        var member = await db.DepartmentMembers.AsNoTracking().SingleAsync(
            m => m.DepartmentId == client.Id && m.UserId == own.Id);
        Assert.That(member.SkillLevel, Is.EqualTo(5));
        Assert.That(member.Weight, Is.EqualTo(2m));
    }

    [Test]
    public async Task Controller_mapeia_membro_herdado_para_422()
    {
        // B2 — o controller devolve 422 (não 400/404) e preserva a mensagem.
        var mediator = new CapturingMediator
        {
            Failure = Error.Validation(
                "UserId",
                "Membro herdado do departamento global: o perfil é gerenciado no departamento global.")
        };

        var controller = new DepartmentsController(mediator, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        var action = await controller.UpdateMemberProfile(
            Guid.NewGuid(), Guid.NewGuid(), new UpdateDepartmentMemberProfileRequest(SkillLevel: 5));

        var objectResult = action as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status422UnprocessableEntity));

        var payload = JsonSerializer.Serialize(objectResult.Value);
        Assert.That(payload, Does.Contain("gerenciado no departamento global"));
    }

    private sealed class FakeMetrics : ITechnicianMetricsService
    {
        public Task<TechnicianMetricsDto> GetMetricsAsync(
            Guid userId, Guid? clientScope = null, CancellationToken ct = default)
            => Task.FromResult(MetricsFor(userId));

        public Task<IReadOnlyList<TechnicianMetricsDto>> GetMetricsForUsersAsync(
            IReadOnlyCollection<Guid> userIds, Guid? clientScope = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TechnicianMetricsDto>>(
                userIds.Select(MetricsFor).ToList());

        public Task<int> RefreshSnapshotsAsync(
            IReadOnlyCollection<Guid>? userIds = null, Guid? departmentId = null, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<MetricsRefreshResult> RefreshDueAsync(CancellationToken ct = default, bool force = false)
            => Task.FromResult(new MetricsRefreshResult(0, 0, 0, new Dictionary<Guid, int>(), 0));

        public Task<MetricsBackfillProgress> RefreshForcedAsync(
            Guid? clientId, DateTime sessionStartUtc, int maxUsers, CancellationToken ct = default)
            => Task.FromResult(new MetricsBackfillProgress(0, 0, false));

        public Task<int> PurgeOrphanSnapshotsAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        private static TechnicianMetricsDto MetricsFor(Guid userId)
            => new(userId, 90, 0, 0, 0, null, null, null, 0, 0, null, 0, null, [], [], DateTime.UtcNow);
    }

    private sealed class CapturingMediator : IMediator
    {
        public Error? Failure { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object response = request is UpdateDepartmentMemberProfileCommand && Failure is not null
                ? Result<DepartmentMemberProfileDto>.Failure(Failure)
                : default(TResponse)!;
            return Task.FromResult((TResponse)response);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class MetricsTestDbContext(DbContextOptions<DiscoveryDbContext> options)
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
