using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Cqrs.Auth.CommandHandlers;
using Discovery.Infrastructure.Cqrs.Behaviors;
using Discovery.Infrastructure.Cqrs.Users;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regressões dos handlers de identidade corrigidos na revisão:
/// - UpdateUserCommandHandler aceitava login/e-mail duplicado e deixava o banco
///   estourar a unique index (500 em vez de 409);
/// - ResetUserPasswordCommandHandler (reset administrativo) não revogava as sessões
///   ativas do usuário alvo.
/// </summary>
[TestFixture]
public class IdentityUserHandlersTests
{
    private static DiscoveryDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"identity-handlers-{Guid.NewGuid():N}")
            .Options;

        return new IdentityTestDbContext(options);
    }

    private static User NewUser(string login, string email) => new()
    {
        Id = Guid.NewGuid(),
        Login = login,
        Email = email,
        FullName = login,
        PasswordHash = "hash",
        PasswordSalt = "salt",
        IsActive = true,
        MfaRequired = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Test]
    public async Task UpdateUser_WithDuplicateLogin_ReturnsConflict_AndDoesNotPersist()
    {
        await using var db = NewDb();
        var existing = NewUser("ana", "ana@empresa.com");
        var target = NewUser("bruno", "bruno@empresa.com");
        db.Users.AddRange(existing, target);
        await db.SaveChangesAsync();

        var handler = new UpdateUserCommandHandler(new UserRepository(db));
        var result = await handler.Handle(
            new UpdateUserCommand(target.Id, existing.Login, null, null, null, null), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Conflict"));

        var persisted = await new UserRepository(db).GetByIdAsync(target.Id);
        Assert.That(persisted!.Login, Is.EqualTo("bruno"));
    }

    [Test]
    public async Task UpdateUser_WithDuplicateEmail_ReturnsConflict()
    {
        await using var db = NewDb();
        var existing = NewUser("ana", "ana@empresa.com");
        var target = NewUser("bruno", "bruno@empresa.com");
        db.Users.AddRange(existing, target);
        await db.SaveChangesAsync();

        var handler = new UpdateUserCommandHandler(new UserRepository(db));
        var result = await handler.Handle(
            new UpdateUserCommand(target.Id, null, "ana@empresa.com", null, null, null), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Conflict"));
    }

    [Test]
    public async Task UpdateUser_KeepingOwnLoginAndEmail_Succeeds()
    {
        await using var db = NewDb();
        var target = NewUser("bruno", "bruno@empresa.com");
        db.Users.Add(target);
        await db.SaveChangesAsync();

        var handler = new UpdateUserCommandHandler(new UserRepository(db));
        var result = await handler.Handle(
            new UpdateUserCommand(target.Id, "  bruno  ", "BRUNO@empresa.com", "  Bruno Lima  ", true, null),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var persisted = await new UserRepository(db).GetByIdAsync(target.Id);
        Assert.That(persisted!.Login, Is.EqualTo("bruno"));
        Assert.That(persisted.Email, Is.EqualTo("BRUNO@empresa.com"));
        Assert.That(persisted.FullName, Is.EqualTo("Bruno Lima"));
    }

    [Test]
    public async Task ResetUserPassword_RevokesAllActiveSessions()
    {
        await using var db = NewDb();
        var user = NewUser("ana", "ana@empresa.com");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var passwordService = new UserPasswordService(new ConfigurationBuilder().Build());
        var userRepo = new UserRepository(db);
        var passwordManagement = new UserPasswordManagementService(
            userRepo, passwordService, NullLogger<UserPasswordManagementService>.Instance);

        var sessions = new RecordingSessionRepository();
        var handler = new ResetUserPasswordCommandHandler(passwordManagement, sessions, userRepo);

        var result = await handler.Handle(
            new ResetUserPasswordCommand(user.Id, "SenhaNova#2026", "admin"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(sessions.RevokeAllCalls, Is.EqualTo(1));
        Assert.That(sessions.RevokedUserId, Is.EqualTo(user.Id));

        var persisted = await userRepo.GetByIdAsync(user.Id);
        Assert.That(
            passwordService.VerifyPassword("SenhaNova#2026", persisted!.PasswordSalt, persisted.PasswordHash),
            Is.True);
    }

    [Test]
    public async Task ResetUserPassword_WithWeakPassword_FailsWithoutRevokingSessions()
    {
        await using var db = NewDb();
        var user = NewUser("ana", "ana@empresa.com");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var passwordService = new UserPasswordService(new ConfigurationBuilder().Build());
        var userRepo = new UserRepository(db);
        var passwordManagement = new UserPasswordManagementService(
            userRepo, passwordService, NullLogger<UserPasswordManagementService>.Instance);

        var sessions = new RecordingSessionRepository();
        var handler = new ResetUserPasswordCommandHandler(passwordManagement, sessions, userRepo);

        var result = await handler.Handle(
            new ResetUserPasswordCommand(user.Id, "curta", "admin"), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(sessions.RevokeAllCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task CreateUser_ThroughMediatRWithTransactionBehavior_SucceedsAndCommitsWithoutNestedTransactionError()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<DiscoveryDbContext>(_ =>
        {
            var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new IdentityTestDbContext(options);
            db.Database.EnsureCreated();
            return db;
        });
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserGroupRepository, UserGroupRepository>();
        services.AddScoped<IPasswordService, UserPasswordService>();
        services.AddScoped<IUserPasswordManagementService, UserPasswordManagementService>();
        services.AddScoped<IPermissionService, StubPermissionService>();
        services.AddScoped<IScopeContext, GlobalScopeContextStub>();
        services.AddScoped<ISiteRepository, EmptySiteRepositoryStub>();
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CreateUserCommandHandler>();
            cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
        });

        await using var sp = services.BuildServiceProvider();
        await using (var seedScope = sp.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            seedDb.UserGroups.Add(new UserGroup
            {
                Id = groupId,
                Name = "Administradores",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await seedDb.SaveChangesAsync();
        }

        await using var scope = sp.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var result = await mediator.Send(new CreateUserCommand(
            "  sektorcloud  ",
            "  daniel@empresa.com  ",
            "  Daniel  ",
            "SenhaForte#2026",
            MfaRequired: true,
            GroupId: groupId,
            RequestedByUserId: actorId));

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(db.Database.CurrentTransaction, Is.Null);

        db.ChangeTracker.Clear();
        var created = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Login == "sektorcloud");
        Assert.That(created, Is.Not.Null);
        Assert.That(created!.Email, Is.EqualTo("daniel@empresa.com"));
        Assert.That(created.FullName, Is.EqualTo("Daniel"));
        Assert.That(created.MfaRequired, Is.True);
        Assert.That(
            scope.ServiceProvider.GetRequiredService<IPasswordService>()
                .VerifyPassword("SenhaForte#2026", created.PasswordSalt, created.PasswordHash),
            Is.True);

        var membership = await db.UserGroupMemberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.GroupId == groupId && m.UserId == created.Id);
        Assert.That(membership, Is.Not.Null);
    }

    private sealed class GlobalScopeContextStub : IScopeContext
    {
        public Guid? UserId { get; private set; }
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
        public void SetUserId(Guid userId) => UserId = userId;
        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess { HasGlobalAccess = true });
        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action)
            => Task.FromResult(true);
    }

    private sealed class StubPermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(
            Guid userId, ResourceType resource, ActionType action,
            ScopeLevel scopeLevel = ScopeLevel.Global, Guid? scopeId = null, Guid? parentScopeId = null)
            => Task.FromResult(true);
        public Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess { HasGlobalAccess = true });
        public Task InvalidateUserCacheAsync(Guid userId) => Task.CompletedTask;
        public Task InvalidateAllCacheAsync() => Task.CompletedTask;
    }

    private sealed class EmptySiteRepositoryStub : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id) => Task.FromResult<Site?>(null);
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>([]);
        public Task<Site> CreateAsync(Site site) => Task.FromResult(site);
        public Task UpdateAsync(Site site) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    /// <summary>Fake mínimo: só a revogação interessa para este teste.</summary>
    private sealed class RecordingSessionRepository : IUserSessionRepository
    {
        public int RevokeAllCalls { get; private set; }
        public Guid? RevokedUserId { get; private set; }

        public Task<bool> RevokeAllByUserIdAsync(Guid userId)
        {
            RevokeAllCalls++;
            RevokedUserId = userId;
            return Task.FromResult(true);
        }

        public Task<UserSession?> GetByIdAsync(Guid id) => Task.FromResult<UserSession?>(null);
        public Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash) => Task.FromResult<UserSession?>(null);
        public Task<UserSession?> GetByRefreshTokenHashWithGracePeriodAsync(string refreshTokenHash) => Task.FromResult<UserSession?>(null);
        public Task<IEnumerable<UserSession>> GetActiveByUserIdAsync(Guid userId) => Task.FromResult<IEnumerable<UserSession>>([]);
        public Task<IReadOnlyList<UserSession>> GetRecentByUserIdAsync(Guid userId, int limit = 30) => Task.FromResult<IReadOnlyList<UserSession>>([]);
        public Task<UserSession> CreateAsync(UserSession session) => Task.FromResult(session);
        public Task<bool> RevokeAsync(Guid sessionId) => Task.FromResult(true);
        public Task<bool> RevokeWithGracePeriodAsync(Guid sessionId, TimeSpan gracePeriod) => Task.FromResult(true);
        public Task<bool> UpdateAccessTokenHashAsync(Guid sessionId, string newHash, DateTime newExpiry) => Task.FromResult(true);
    }

    /// <summary>Contexto restrito a User/UserSession (InMemory não suporta o modelo completo).</summary>
    private sealed class IdentityTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(User).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(User)
                             && type != typeof(UserSession)
                             && type != typeof(UserGroup)
                             && type != typeof(UserGroupMembership)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(u => u.Id);
                entity.Property(u => u.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<UserSession>(entity =>
            {
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<UserGroup>(entity =>
            {
                entity.HasKey(g => g.Id);
                entity.Property(g => g.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<UserGroupMembership>(entity =>
            {
                entity.HasKey(m => new { m.GroupId, m.UserId });
            });
        }
    }
}