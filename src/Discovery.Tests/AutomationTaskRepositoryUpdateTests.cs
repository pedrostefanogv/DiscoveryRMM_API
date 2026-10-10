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
/// Regressão do bug "salva mas não muda o modo": o update do repositório
/// recopia campo a campo uma entidade DESTACADA (GetByIdAsync usa AsNoTracking),
/// então só o que está na lista explícita é persistido. NotificationMode e
/// ToastTiming ficaram fora dela: o PUT respondia sucesso, RequiresApproval
/// mudava, mas o modo continuava "Prompt" e o agente seguia exibindo o Welcome
/// do PSADT — exatamente o sintoma relatado na edição da tarefa Brave.
/// </summary>
[TestFixture]
public class AutomationTaskRepositoryUpdateTests
{
    private static DiscoveryDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"automation-task-update-{Guid.NewGuid():N}")
            .Options;
        return new AutomationTaskTestDbContext(options);
    }

    /// <summary>Contexto restrito a AutomationTaskDefinition (o modelo completo não
    /// sobe no provider InMemory).</summary>
    private sealed class AutomationTaskTestDbContext(DbContextOptions<DiscoveryDbContext> options)
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

    private static AutomationTaskDefinition NewTask() => new()
    {
        Name = "Brave",
        ActionType = AutomationTaskActionType.InstallPackage,
        InstallationType = AppInstallationType.Winget,
        PackageId = "brave.brave",
        ScopeType = AppApprovalScopeType.Site,
        SiteId = Guid.NewGuid(),
        RequiresApproval = true,
        NotificationMode = AutomationNotificationMode.Prompt,
        ToastTiming = AutomationToastTiming.After,
        IsActive = true,
    };

    [Test]
    public async Task UpdateAsync_PersistsNotificationModeAndToastTiming()
    {
        await using var db = NewDb();
        var repo = new AutomationTaskRepository(db);
        var created = await repo.CreateAsync(NewTask());

        // O serviço busca com AsNoTracking e devolve uma entidade DESTACADA.
        var detached = await repo.GetByIdAsync(created.Id, includeInactive: true);
        Assert.That(detached, Is.Not.Null, "tarefa deveria existir");

        detached!.NotificationMode = AutomationNotificationMode.Silent;
        detached.ToastTiming = AutomationToastTiming.Before;
        detached.RequiresApproval = false;

        await repo.UpdateAsync(detached);

        var reloaded = await repo.GetByIdAsync(created.Id, includeInactive: true);
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!.NotificationMode, Is.EqualTo(AutomationNotificationMode.Silent),
            "o modo escolhido (Silencioso) precisa sobreviver ao update do repositório");
        Assert.That(reloaded.ToastTiming, Is.EqualTo(AutomationToastTiming.Before),
            "o momento do toast precisa sobreviver ao update do repositório");
        Assert.That(reloaded.RequiresApproval, Is.False);
    }
}