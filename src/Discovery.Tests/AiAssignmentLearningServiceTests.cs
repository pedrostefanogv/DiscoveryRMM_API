using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Ciclo de aprendizado híbrido: extração de competências e recalibração de pesos,
/// nos modos Sugerir e Automático, com rastreabilidade das aplicações automáticas.
/// </summary>
public class AiAssignmentLearningServiceTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("ai-learning-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new LearningTestDbContext(options);
    }

    private static AiAssignmentLearningService BuildService(DiscoveryDbContext db, out FakeConfigurationAudit audit)
    {
        audit = new FakeConfigurationAudit();
        return new AiAssignmentLearningService(db, audit, NullLogger<AiAssignmentLearningService>.Instance);
    }

    private static async Task<(Department Department, User User, Guid DepartmentId)> SeedSkillsAsync(
        DiscoveryDbContext db, AiLearningMode mode, int resolvedTickets = 3)
    {
        var now = DateTime.UtcNow;
        var user = new User { Id = Guid.NewGuid(), Login = "ana", Email = "ana@x.com", FullName = "Ana", IsActive = true };
        db.Users.Add(user);

        var department = new Department
        {
            Id = Guid.NewGuid(),
            Name = "Suporte",
            AssignmentStrategy = (int)TicketAssignmentStrategy.AiTriage,
            AiSkillLearningMode = (int)mode,
            AiSkillMinEvidence = 2,
            AiSkillMaxTags = 10,
            AiWeightLearningMode = (int)AiLearningMode.Off,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Departments.Add(department);

        db.DepartmentMembers.Add(new DepartmentMember
        {
            Id = Guid.NewGuid(),
            DepartmentId = department.Id,
            UserId = user.Id,
            IsActive = true,
            CreatedAt = now
        });

        for (var i = 0; i < resolvedTickets; i++)
        {
            db.Tickets.Add(new Ticket
            {
                Id = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                WorkflowStateId = Guid.NewGuid(),
                AssignedToUserId = user.Id,
                Title = "Falha de rede no switch " + i,
                Description = "A rede caiu e o switch reiniciou",
                Category = "Rede",
                Priority = TicketPriority.Medium,
                CreatedAt = now.AddDays(-5),
                UpdatedAt = now.AddDays(-5),
                ClosedAt = now.AddDays(-4)
            });
        }

        await db.SaveChangesAsync();
        return (department, user, department.Id);
    }

    private static AssignmentCandidateDto Candidate(Guid userId, double csat)
        => new(userId, null, 0.5, 0.5, 0.5, 0.5, 0.5, csat, 0.5, false, 0, null);

    private static async Task<Guid> SeedWeightsAsync(DiscoveryDbContext db)
    {
        var now = DateTime.UtcNow;
        var department = new Department
        {
            Id = Guid.NewGuid(),
            Name = "Triagem",
            AssignmentStrategy = (int)TicketAssignmentStrategy.AiTriage,
            AiWeightLearningMode = (int)AiLearningMode.Suggest,
            AiSkillLearningMode = (int)AiLearningMode.Off,
            AiWeightCycleDays = 7,
            AiWeightMaxDeltaPerCycle = 0.10m,
            AiWeightMin = 0.05m,
            AiWeightMax = 0.50m,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Departments.Add(department);

        var chosenId = Guid.NewGuid();

        for (var i = 0; i < 24; i++)
        {
            var overridden = i < 12;
            // Metade: o escolhido tinha CSAT acima da mediana e foi trocado;
            // metade: CSAT abaixo da mediana e não foi trocado.
            var chosenCsat = overridden ? 0.9 : 0.1;
            var otherCsat = overridden ? 0.1 : 0.9;

            var candidates = JsonSerializer.Serialize(new List<AssignmentCandidateDto>
            {
                Candidate(chosenId, chosenCsat),
                Candidate(Guid.NewGuid(), otherCsat)
            });

            db.TicketAssignmentDecisions.Add(new TicketAssignmentDecision
            {
                Id = Guid.NewGuid(),
                TicketId = Guid.NewGuid(),
                DepartmentId = department.Id,
                Mode = 1,
                StrategySource = AiAssignmentDecisionSource.Ai,
                ChosenUserId = chosenId,
                Applied = true,
                CandidatesJson = candidates,
                OverriddenAt = overridden ? now.AddHours(-2) : null,
                CreatedAt = now.AddDays(-1)
            });
        }

        await db.SaveChangesAsync();
        return department.Id;
    }

    [Test]
    public async Task SkillCycle_InAutoMode_AppliesTagsAndAudits()
    {
        await using var db = CreateDb();
        var (_, user, departmentId) = await SeedSkillsAsync(db, AiLearningMode.Auto);
        var service = BuildService(db, out var audit);

        var created = await service.RunCycleAsync(departmentId, null, CancellationToken.None);

        Assert.That(created, Is.EqualTo(1));

        var member = await db.DepartmentMembers.AsNoTracking().SingleAsync();
        Assert.That(member.SkillTagsJson, Does.Contain("rede"));
        Assert.That(member.UserId, Is.EqualTo(user.Id));

        var suggestion = await db.TechnicianSkillSuggestions.AsNoTracking().SingleAsync();
        Assert.That(suggestion.Status, Is.EqualTo("applied"));
        Assert.That(suggestion.AutoApplied, Is.True);

        Assert.That(audit.Changes, Has.Count.EqualTo(1), "aplicação automática registra auditoria de configuração");
        Assert.That(audit.Changes[0].EntityType, Is.EqualTo("DepartmentMember"));
    }

    [Test]
    public async Task SkillCycle_InSuggestMode_KeepsPendingWithoutChangingProfile()
    {
        await using var db = CreateDb();
        var (_, _, departmentId) = await SeedSkillsAsync(db, AiLearningMode.Suggest);
        var service = BuildService(db, out var audit);

        var created = await service.RunCycleAsync(departmentId, null, CancellationToken.None);

        Assert.That(created, Is.EqualTo(1));

        var member = await db.DepartmentMembers.AsNoTracking().SingleAsync();
        Assert.That(member.SkillTagsJson, Is.Null, "no modo Sugerir o perfil não muda");

        var suggestion = await db.TechnicianSkillSuggestions.AsNoTracking().SingleAsync();
        Assert.That(suggestion.Status, Is.EqualTo("pending"));
        Assert.That(audit.Changes, Is.Empty, "sugestão pendente não é mudança de configuração");

        var pending = await service.GetSuggestionsAsync(departmentId, CancellationToken.None);
        Assert.That(pending.Skills, Has.Count.EqualTo(1));
        Assert.That(pending.Skills[0].SuggestedTags, Does.Contain("rede"));
    }

    [Test]
    public async Task WeightCycle_InSuggestMode_GeneratesProposal()
    {
        await using var db = CreateDb();
        var departmentId = await SeedWeightsAsync(db);
        var service = BuildService(db, out var audit);

        var created = await service.RunCycleAsync(departmentId, null, CancellationToken.None);

        Assert.That(created, Is.EqualTo(1));

        var suggestion = await db.AiWeightSuggestions.AsNoTracking().SingleAsync();
        Assert.That(suggestion.Status, Is.EqualTo("pending"));

        var suggested = JsonSerializer.Deserialize<AiAssignmentWeightsDto>(suggestion.SuggestedWeightsJson)!;
        Assert.That(suggested.Csat, Is.LessThan(0.10), "CSAT supervalorizado reduz o peso");
        Assert.That(audit.Changes, Is.Empty);

        var pending = await service.GetSuggestionsAsync(departmentId, CancellationToken.None);
        Assert.That(pending.Weights, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ApplyWeightSuggestion_PersistsWeightsAndAudits()
    {
        await using var db = CreateDb();
        var departmentId = await SeedWeightsAsync(db);
        var service = BuildService(db, out _);

        await service.RunCycleAsync(departmentId, null, CancellationToken.None);
        var pending = await service.GetSuggestionsAsync(departmentId, CancellationToken.None);
        var applied = await service.ApplyWeightSuggestionAsync(
            departmentId, pending.Weights[0].Id, Guid.NewGuid(), CancellationToken.None);

        Assert.That(applied, Is.Not.Null);

        var department = await db.Departments.AsNoTracking().SingleAsync();
        Assert.That(department.AiAssignmentWeightsJson, Is.Not.Null);

        var remaining = await service.GetSuggestionsAsync(departmentId, CancellationToken.None);
        Assert.That(remaining.Weights, Is.Empty, "sugestão aplicada sai da lista de pendentes");
    }

    [Test]
    public async Task Cycle_SkipsDepartmentsWithoutAiTriage()
    {
        await using var db = CreateDb();
        var (department, _, _) = await SeedSkillsAsync(db, AiLearningMode.Auto, resolvedTickets: 5);

        var tracked = await db.Departments.FirstAsync(d => d.Id == department.Id);
        tracked.AssignmentStrategy = (int)TicketAssignmentStrategy.RoundRobin;
        await db.SaveChangesAsync();

        var service = BuildService(db, out _);
        var created = await service.RunCycleAsync(department.Id, null, CancellationToken.None);

        Assert.That(created, Is.EqualTo(0));
        Assert.That(await db.TechnicianSkillSuggestions.AsNoTracking().CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Cycle_WithLearningOff_DoesNothing()
    {
        await using var db = CreateDb();
        var (_, _, departmentId) = await SeedSkillsAsync(db, AiLearningMode.Off, resolvedTickets: 5);
        var service = BuildService(db, out _);

        var created = await service.RunCycleAsync(departmentId, null, CancellationToken.None);

        Assert.That(created, Is.EqualTo(0));
        Assert.That(await db.TechnicianSkillSuggestions.AsNoTracking().CountAsync(), Is.EqualTo(0));
    }

    private sealed class FakeConfigurationAudit : IConfigurationAuditService
    {
        public List<(string EntityType, Guid EntityId, string Field, string? OldValue, string? NewValue)> Changes { get; } = [];

        public Task LogChangeAsync(string entityType, Guid entityId, string fieldName,
            string? oldValue, string? newValue, string? reason = null, string? changedBy = null, string? ipAddress = null)
        {
            Changes.Add((entityType, entityId, fieldName, oldValue, newValue));
            return Task.CompletedTask;
        }

        public Task<IEnumerable<ConfigurationAudit>> GetEntityHistoryAsync(string entityType, Guid entityId, int limit = 100)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetRecentChangesAsync(int days = 90, int limit = 1000)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetChangesByUserAsync(string username, int limit = 100)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetFieldHistoryAsync(string entityType, Guid entityId, string fieldName)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetAuditReportAsync(DateTime startDate, DateTime endDate)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);
    }

    private sealed class LearningTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(User), typeof(Client), typeof(Department), typeof(DepartmentMember),
                typeof(Ticket), typeof(TechnicianSkillSuggestion), typeof(AiWeightSuggestion),
                typeof(TicketAssignmentDecision)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<User>(e => e.HasKey(u => u.Id));
            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<DepartmentMember>(e => e.HasKey(m => m.Id));
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<TechnicianSkillSuggestion>(e => e.HasKey(s => s.Id));
            modelBuilder.Entity<AiWeightSuggestion>(e => e.HasKey(s => s.Id));
            modelBuilder.Entity<TicketAssignmentDecision>(e => e.HasKey(d => d.Id));
        }
    }
}
