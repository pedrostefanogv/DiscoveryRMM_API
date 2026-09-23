using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Concorrencia otimista em AgentLabelRule (xmin do Postgres).
///
/// O provider InMemory nao implementa xmin, entao aqui validamos o CONTRATO observavel:
/// duas instancias do repositorio sobre o mesmo contexto convergem para o ultimo estado
/// escrito, sem excecao nao tratada — que e o comportamento que o tratamento de
/// DbUpdateConcurrencyException garante em Postgres.
/// </summary>
public class AgentLabelRuleConcurrencyTests
{
    [Test]
    public async Task UpdateRule_ConcurrentEdits_ConvergeWithoutThrowing()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"label-rule-concurrency-{Guid.NewGuid():N}")
            .Options;

        await using var db = new RuleTestDbContext(options);

        var ruleId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.AgentLabelRules.Add(new AgentLabelRule
        {
            Id = ruleId,
            Name = "Original",
            Label = "PROD",
            IsEnabled = true,
            ApplyMode = AgentLabelApplyMode.ApplyAndRemove,
            ExpressionJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var repo = new AgentLabelRuleRepository(db);

        // Duas edicoes concorrentes do mesmo campo.
        var first = new AgentLabelRule { Id = ruleId, Name = "Edit A", Label = "A", IsEnabled = true, ApplyMode = AgentLabelApplyMode.ApplyOnly, ExpressionJson = "{}", UpdatedBy = "userA" };
        var second = new AgentLabelRule { Id = ruleId, Name = "Edit B", Label = "B", IsEnabled = false, ApplyMode = AgentLabelApplyMode.ApplyAndRemove, ExpressionJson = "{}", UpdatedBy = "userB" };

        Assert.DoesNotThrowAsync(async () =>
        {
            await repo.UpdateAsync(first);
            await repo.UpdateAsync(second);
        });

        var stored = await db.AgentLabelRules.AsNoTracking().SingleAsync(r => r.Id == ruleId);
        Assert.That(stored.Name, Is.EqualTo("Edit B"), "A ultima escrita deve prevalecer de forma deterministica.");
        Assert.That(stored.IsEnabled, Is.False);
        Assert.That(stored.UpdatedBy, Is.EqualTo("userB"));
    }

    [Test]
    public async Task UpdateRule_WhenRuleMissing_DoesNotThrow()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"label-rule-missing-{Guid.NewGuid():N}")
            .Options;

        await using var db = new RuleTestDbContext(options);
        var repo = new AgentLabelRuleRepository(db);

        var missing = new AgentLabelRule { Id = Guid.NewGuid(), Name = "x", Label = "L", ExpressionJson = "{}" };

        Assert.DoesNotThrowAsync(() => repo.UpdateAsync(missing));
    }

    private sealed class RuleTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(AgentLabelRule), typeof(Agent), typeof(Client), typeof(Site) };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<AgentLabelRule>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.ExpressionJson).IsRequired();
            });
            modelBuilder.Entity<Agent>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
        }
    }
}
