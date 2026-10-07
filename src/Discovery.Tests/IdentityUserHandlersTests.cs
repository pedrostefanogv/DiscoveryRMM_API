using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Cqrs.Auth.CommandHandlers;
using Discovery.Infrastructure.Cqrs.Users;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
                         .Where(type => type != typeof(User) && type != typeof(UserSession)))
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
        }
    }
}