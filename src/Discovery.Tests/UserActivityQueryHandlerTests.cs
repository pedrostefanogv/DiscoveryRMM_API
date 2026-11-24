using Discovery.Core.Cqrs.Users.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Cqrs.Users;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

[TestFixture]
public class UserActivityQueryHandlerTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("user-activity-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new UserActivityTestDbContext(options);
    }

    private sealed class UserActivityTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(User),
                typeof(UserSession),
                typeof(AuthAuditLog),
                typeof(Agent),
                typeof(RemoteSession),
                typeof(Ticket),
                typeof(TicketActivityLog),
                typeof(ConfigurationAudit),
                typeof(AutomationScriptAudit),
                typeof(AutomationTaskAudit),
                typeof(PatchApprovalAudit),
                typeof(AppApprovalAudit),
                typeof(ReportExecution),
            };

            foreach (var entityType in typeof(User).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<User>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<UserSession>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<AuthAuditLog>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<Agent>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<RemoteSession>(e =>
            {
                e.HasKey(x => x.Id);
                e.Ignore(x => x.Agent);
                e.Ignore(x => x.Recording);
                e.Ignore(x => x.Audits);
            });
            modelBuilder.Entity<Ticket>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<TicketActivityLog>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<ConfigurationAudit>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<AutomationScriptAudit>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<AutomationTaskAudit>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<PatchApprovalAudit>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<AppApprovalAudit>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<ReportExecution>(e => e.HasKey(x => x.Id));
        }
    }

    [Test]
    public async Task Handle_returns_consolidated_activity_ordered_desc_without_leaking_other_users()
    {
        await using var db = CreateDb();
        var now = DateTime.UtcNow;

        var targetUser = new User
        {
            Id = Guid.NewGuid(),
            Login = "pedro.silva",
            Email = "pedro@empresa.com",
            FullName = "Pedro Silva",
            CreatedAt = now.AddDays(-30),
            UpdatedAt = now.AddDays(-1),
        };
        var otherUser = new User
        {
            Id = Guid.NewGuid(),
            Login = "maria.souza",
            Email = "maria@empresa.com",
            FullName = "Maria Souza",
            CreatedAt = now.AddDays(-20),
            UpdatedAt = now.AddDays(-1),
        };
        db.Users.AddRange(targetUser, otherUser);

        var clientId = Guid.NewGuid();
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Hostname = "SRV-01",
            DisplayName = "Servidor Principal",
            SiteId = Guid.NewGuid(),
        };
        db.Agents.Add(agent);

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Title = "VPN sem acesso",
            ClientId = clientId,
            SiteId = agent.SiteId,
        };
        db.Tickets.Add(ticket);

        db.AuthAuditLogs.AddRange(
            new AuthAuditLog
            {
                Id = Guid.NewGuid(),
                UserId = targetUser.Id,
                EventType = "login_success",
                Success = true,
                IpAddress = "10.0.0.10",
                OccurredAt = now.AddMinutes(-10),
            },
            new AuthAuditLog
            {
                Id = Guid.NewGuid(),
                UserId = otherUser.Id,
                EventType = "identity.user_updated",
                ActorUserId = targetUser.Id,
                ActorLogin = targetUser.Login,
                TargetType = "User",
                TargetId = otherUser.Id,
                Success = true,
                OccurredAt = now.AddMinutes(-8),
            },
            new AuthAuditLog
            {
                Id = Guid.NewGuid(),
                UserId = targetUser.Id,
                EventType = "identity.user_mfa_revoked",
                ActorUserId = otherUser.Id,
                ActorLogin = otherUser.Login,
                TargetType = "User",
                TargetId = targetUser.Id,
                Success = true,
                OccurredAt = now.AddMinutes(-6),
            },
            new AuthAuditLog
            {
                Id = Guid.NewGuid(),
                UserId = otherUser.Id,
                EventType = "login_failed",
                Success = false,
                FailureReason = "invalid_password",
                OccurredAt = now.AddMinutes(-1),
            });

        db.RemoteSessions.AddRange(
            new RemoteSession
            {
                Id = Guid.NewGuid(),
                AgentId = agent.Id,
                UserId = targetUser.Id,
                TenantId = clientId,
                SiteId = agent.SiteId,
                Kind = RemoteSessionKind.Screen,
                Status = "closed",
                StartedAt = now.AddMinutes(-5),
                ExpiresAt = now.AddMinutes(25),
                DurationSeconds = 120,
            },
            new RemoteSession
            {
                Id = Guid.NewGuid(),
                AgentId = agent.Id,
                UserId = otherUser.Id,
                TenantId = clientId,
                SiteId = agent.SiteId,
                Kind = RemoteSessionKind.Terminal,
                Status = "closed",
                StartedAt = now.AddMinutes(-2),
                ExpiresAt = now.AddMinutes(28),
            });

        db.TicketActivityLogs.Add(new TicketActivityLog
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            Type = TicketActivityType.Commented,
            ChangedByUserId = targetUser.Id,
            Comment = "Verificando rota",
            CreatedAt = now.AddMinutes(-4),
        });

        db.ConfigurationAudits.Add(new ConfigurationAudit
        {
            Id = Guid.NewGuid(),
            EntityType = ConfigurationEntityType.Server,
            EntityId = Guid.NewGuid(),
            FieldName = "SessionTimeoutMinutes",
            OldValue = "30",
            NewValue = "60",
            ChangedBy = "PEDRO.SILVA",
            ChangedAt = now.AddMinutes(-3),
        });

        db.AutomationScriptAudits.Add(new AutomationScriptAudit
        {
            Id = Guid.NewGuid(),
            ScriptId = Guid.NewGuid(),
            ChangeType = AutomationScriptChangeType.Updated,
            ChangedBy = targetUser.Email,
            Reason = "Ajuste de timeout",
            ChangedAt = now.AddMinutes(-2),
        });

        db.PatchApprovalAudits.Add(new PatchApprovalAudit
        {
            Id = Guid.NewGuid(),
            ChangeType = PatchApprovalAuditChangeType.Created,
            ScopeType = PatchApprovalScopeType.Global,
            UpdateKey = "KB500123",
            KbId = "KB500123",
            NewAction = PatchApprovalActionType.Approve,
            ChangedBy = targetUser.Login,
            ChangedAt = now.AddMinutes(-1),
        });

        db.ReportExecutions.Add(new ReportExecution
        {
            Id = Guid.NewGuid(),
            TemplateId = Guid.NewGuid(),
            Format = ReportFormat.Xlsx,
            Status = ReportExecutionStatus.Completed,
            RowCount = 42,
            CreatedBy = targetUser.Login,
            CreatedAt = now,
        });

        await db.SaveChangesAsync();

        var handler = new GetUserActivityQueryHandler(db);
        var result = await handler.Handle(new GetUserActivityQuery(targetUser.Id, null, 100), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var dto = result.Value!;
        Assert.That(dto.Items.Count, Is.EqualTo(9));
        Assert.That(dto.Items[0].Category, Is.EqualTo("reports"));
        Assert.That(dto.Items[^1].Category, Is.EqualTo("auth"));
        Assert.That(dto.Items.Any(i => i.FailureReason == "invalid_password"), Is.False);
        Assert.That(dto.Items.Single(i => i.Category == "remote").Title, Does.Contain("Servidor Principal"));
        Assert.That(dto.Items.Single(i => i.Category == "tickets").Title, Does.Contain("VPN sem acesso"));
        Assert.That(dto.CategoryCounts["identity"], Is.EqualTo(2));
    }

    [Test]
    public async Task Handle_filters_by_category_and_returns_not_found_for_missing_user()
    {
        await using var db = CreateDb();
        var user = new User { Id = Guid.NewGuid(), Login = "ana", Email = "ana@x.com", FullName = "Ana" };
        db.Users.Add(user);
        db.AuthAuditLogs.AddRange(
            new AuthAuditLog { Id = Guid.NewGuid(), UserId = user.Id, EventType = "login_success", Success = true, OccurredAt = DateTime.UtcNow },
            new AuthAuditLog { Id = Guid.NewGuid(), UserId = user.Id, EventType = "identity.user_updated", ActorUserId = user.Id, Success = true, OccurredAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var handler = new GetUserActivityQueryHandler(db);

        var authOnly = await handler.Handle(new GetUserActivityQuery(user.Id, "auth", 50), CancellationToken.None);
        Assert.That(authOnly.IsSuccess, Is.True);
        Assert.That(authOnly.Value!.Items.Count, Is.EqualTo(1));
        Assert.That(authOnly.Value.Items[0].EventType, Is.EqualTo("login_success"));

        var missing = await handler.Handle(new GetUserActivityQuery(Guid.NewGuid()), CancellationToken.None);
        Assert.That(missing.IsSuccess, Is.False);
        Assert.That(missing.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task Handle_applies_Days_filter_and_describes_user_unlocked_event()
    {
        await using var db = CreateDb();
        var user = new User { Id = Guid.NewGuid(), Login = "carlos", Email = "carlos@x.com", FullName = "Carlos" };
        db.Users.Add(user);
        db.AuthAuditLogs.AddRange(
            new AuthAuditLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                EventType = "identity.user_unlocked",
                ActorUserId = Guid.NewGuid(),
                ActorLogin = "admin",
                TargetType = "User",
                TargetId = user.Id,
                Success = true,
                OccurredAt = DateTime.UtcNow.AddDays(-2),
            },
            new AuthAuditLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                EventType = "login_success",
                Success = true,
                OccurredAt = DateTime.UtcNow.AddDays(-45),
            });
        await db.SaveChangesAsync();

        var handler = new GetUserActivityQueryHandler(db);
        var result = await handler.Handle(new GetUserActivityQuery(user.Id, null, 100, Days: 7), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Items.Count, Is.EqualTo(1));
        Assert.That(result.Value.Items[0].Title, Is.EqualTo("Conta desbloqueada por administrador"));
    }

    [Test]
    public async Task SessionRepository_GetRecentByUserIdAsync_returns_active_and_revoked_ordered_desc()
    {
        await using var db = CreateDb();
        var repo = new UserSessionRepository(db);
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        await repo.CreateAsync(new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccessTokenHash = "h1",
            RefreshTokenHash = "r1",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            RevokedAt = DateTime.UtcNow.AddMinutes(-10),
        });
        await Task.Delay(10);
        await repo.CreateAsync(new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccessTokenHash = "h2",
            RefreshTokenHash = "r2",
            ExpiresAt = DateTime.UtcNow.AddHours(2),
        });
        await repo.CreateAsync(new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = otherUserId,
            AccessTokenHash = "h3",
            RefreshTokenHash = "r3",
            ExpiresAt = DateTime.UtcNow.AddHours(2),
        });

        var sessions = await repo.GetRecentByUserIdAsync(userId, 30);
        Assert.That(sessions.Count, Is.EqualTo(2));
        Assert.That(sessions[0].IsValid, Is.True);
        Assert.That(sessions[1].IsRevoked, Is.True);
    }
}
