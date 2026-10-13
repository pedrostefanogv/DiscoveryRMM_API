using System;
using System.Linq;
using System.Threading.Tasks;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Filtros da listagem de tarefas por trigger e status (isActive).
/// O filtro por trigger é server-side (OR entre as flags selecionadas) para
/// funcionar junto da paginação por cursor, sem depender do que já veio na página.
/// </summary>
[TestFixture]
public class AutomationTaskRepositoryFilterTests
{
    private static DiscoveryDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"automation-task-filters-{Guid.NewGuid():N}")
            .Options;
        return new AutomationTaskFilterTestDbContext(options);
    }

    /// <summary>Contexto restrito a AutomationTaskDefinition (o modelo completo não
    /// sobe no provider InMemory).</summary>
    private sealed class AutomationTaskFilterTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(AutomationTaskDefinition).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(AutomationTaskDefinition)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<AutomationTaskDefinition>(entity => entity.HasKey(t => t.Id));
        }
    }

    private static AutomationTaskDefinition NewTask(
        string name,
        bool immediate = false,
        bool recurring = false,
        bool onUserLogin = false,
        bool onAgentCheckIn = false,
        bool isActive = true,
        AutomationTaskActionType actionType = AutomationTaskActionType.RunScript) => new()
    {
        Name = name,
        ActionType = actionType,
        ScopeType = AppApprovalScopeType.Global,
        TriggerImmediate = immediate,
        TriggerRecurring = recurring,
        TriggerOnUserLogin = onUserLogin,
        TriggerOnAgentCheckIn = onAgentCheckIn,
        IsActive = isActive,
    };

    private static Task<System.Collections.Generic.IReadOnlyList<AutomationTaskDefinition>> ListAsync(
        AutomationTaskRepository repo,
        System.Collections.Generic.IReadOnlyList<string>? triggers = null,
        bool? isActive = null,
        System.Collections.Generic.IReadOnlyList<AutomationTaskActionType>? actionTypes = null)
        => repo.GetListPageAsync(
            scopeType: null, scopeId: null,
            activeOnly: false, deletedOnly: false, includeDeleted: false,
            search: null, clientId: null, siteId: null, agentId: null,
            scopeTypes: null, actionTypes: actionTypes,
            triggers: triggers, isActive: isActive,
            cursor: null, limit: 50);

    [Test]
    public async Task Triggers_FilterReturnsOnlyTasksWithTheSelectedTrigger()
    {
        await using var db = NewDb();
        var repo = new AutomationTaskRepository(db);
        await repo.CreateAsync(NewTask("imediata", immediate: true));
        await repo.CreateAsync(NewTask("recorrente", recurring: true));
        await repo.CreateAsync(NewTask("login", onUserLogin: true));

        var result = await ListAsync(repo, triggers: new[] { "Immediate" });

        Assert.That(result.Select(t => t.Name), Is.EquivalentTo(new[] { "imediata" }));
    }

    [Test]
    public async Task Triggers_MultipleValuesUseOrSemantics()
    {
        await using var db = NewDb();
        var repo = new AutomationTaskRepository(db);
        await repo.CreateAsync(NewTask("imediata", immediate: true));
        await repo.CreateAsync(NewTask("recorrente", recurring: true));
        await repo.CreateAsync(NewTask("checkin", onAgentCheckIn: true));
        await repo.CreateAsync(NewTask("login", onUserLogin: true));

        var result = await ListAsync(repo, triggers: new[] { "Recurring", "OnUserLogin" });

        Assert.That(result.Select(t => t.Name), Is.EquivalentTo(new[] { "recorrente", "login" }));
    }

    [Test]
    public async Task Triggers_AreCaseInsensitive()
    {
        await using var db = NewDb();
        var repo = new AutomationTaskRepository(db);
        await repo.CreateAsync(NewTask("imediata", immediate: true));

        var result = await ListAsync(repo, triggers: new[] { "immediate" });

        Assert.That(result.Select(t => t.Name), Is.EquivalentTo(new[] { "imediata" }));
    }

    [Test]
    public async Task Triggers_UnknownValueMatchesNothing()
    {
        await using var db = NewDb();
        var repo = new AutomationTaskRepository(db);
        await repo.CreateAsync(NewTask("imediata", immediate: true));

        var result = await ListAsync(repo, triggers: new[] { "NaoExiste" });

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task IsActive_FalseReturnsOnlyInactiveTasks()
    {
        await using var db = NewDb();
        var repo = new AutomationTaskRepository(db);
        await repo.CreateAsync(NewTask("ativa"));
        await repo.CreateAsync(NewTask("inativa", isActive: false));

        var result = await ListAsync(repo, isActive: false);

        Assert.That(result.Select(t => t.Name), Is.EquivalentTo(new[] { "inativa" }));
    }

    [Test]
    public async Task IsActive_TrueReturnsOnlyActiveTasks()
    {
        await using var db = NewDb();
        var repo = new AutomationTaskRepository(db);
        await repo.CreateAsync(NewTask("ativa"));
        await repo.CreateAsync(NewTask("inativa", isActive: false));

        var result = await ListAsync(repo, isActive: true);

        Assert.That(result.Select(t => t.Name), Is.EquivalentTo(new[] { "ativa" }));
    }

    [Test]
    public async Task TriggersAndStatusCombine()
    {
        await using var db = NewDb();
        var repo = new AutomationTaskRepository(db);
        await repo.CreateAsync(NewTask("imediata-ativa", immediate: true));
        await repo.CreateAsync(NewTask("imediata-inativa", immediate: true, isActive: false));
        await repo.CreateAsync(NewTask("recorrente-ativa", recurring: true));

        var result = await ListAsync(repo, triggers: new[] { "Immediate" }, isActive: true);

        Assert.That(result.Select(t => t.Name), Is.EquivalentTo(new[] { "imediata-ativa" }));
    }

    [Test]
    public async Task TriggersAndActionTypeCombine()
    {
        await using var db = NewDb();
        var repo = new AutomationTaskRepository(db);
        await repo.CreateAsync(NewTask("script-imediato", immediate: true, actionType: AutomationTaskActionType.RunScript));
        await repo.CreateAsync(NewTask("pacote-imediato", immediate: true, actionType: AutomationTaskActionType.InstallPackage));

        var result = await ListAsync(
            repo,
            triggers: new[] { "Immediate" },
            actionTypes: new[] { AutomationTaskActionType.InstallPackage });

        Assert.That(result.Select(t => t.Name), Is.EquivalentTo(new[] { "pacote-imediato" }));
    }

    [Test]
    public async Task CountAsync_AppliesTriggerAndStatusFilters()
    {
        await using var db = NewDb();
        var repo = new AutomationTaskRepository(db);
        await repo.CreateAsync(NewTask("imediata-ativa", immediate: true));
        await repo.CreateAsync(NewTask("imediata-inativa", immediate: true, isActive: false));
        await repo.CreateAsync(NewTask("recorrente-ativa", recurring: true));

        var count = await repo.CountAsync(
            scopeType: null, scopeId: null,
            activeOnly: false, deletedOnly: false, includeDeleted: false,
            search: null, clientId: null, siteId: null, agentId: null,
            scopeTypes: null, actionTypes: null,
            triggers: new[] { "Immediate" }, isActive: true);

        Assert.That(count, Is.EqualTo(1));
    }
}
