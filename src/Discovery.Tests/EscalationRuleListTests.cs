using Discovery.Core.Cqrs.EscalationRules.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.EscalationRules;

namespace Discovery.Tests;

/// <summary>
/// Testa a listagem de regras de escalonamento: com perfil retorna as do perfil;
/// sem perfil retorna as ativas de todos os perfis (antes vinha lista vazia).
/// </summary>
public class EscalationRuleListTests
{
    [Test]
    public async Task ListWithoutProfile_ReturnsActiveRulesFromAllProfiles()
    {
        var a = BuildRule(Guid.NewGuid(), "Regra A", isActive: true);
        var b = BuildRule(Guid.NewGuid(), "Regra B", isActive: true);
        var inactive = BuildRule(Guid.NewGuid(), "Inativa", isActive: false);
        var handler = new ListEscalationRulesQueryHandler(new FakeEscalationRuleService(a, b, inactive));

        var result = await handler.Handle(new ListEscalationRulesQuery(null), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Select(r => r.Name), Is.EquivalentTo(new[] { "Regra A", "Regra B" }));
    }

    [Test]
    public async Task ListWithProfile_ReturnsOnlyThatProfileRules()
    {
        var profileId = Guid.NewGuid();
        var a = BuildRule(profileId, "Regra A", isActive: true);
        var other = BuildRule(Guid.NewGuid(), "Regra B", isActive: true);
        var handler = new ListEscalationRulesQueryHandler(new FakeEscalationRuleService(a, other));

        var result = await handler.Handle(new ListEscalationRulesQuery(profileId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Select(r => r.Name), Is.EquivalentTo(new[] { "Regra A" }));
    }

    private static TicketEscalationRule BuildRule(Guid workflowProfileId, string name, bool isActive) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowProfileId = workflowProfileId,
        Name = name,
        TriggerAtSlaPercent = 80,
        TriggerAtHoursBefore = 0,
        BumpPriority = false,
        NotifyAssignee = true,
        IsActive = isActive,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private sealed class FakeEscalationRuleService(params TicketEscalationRule[] rules) : IEscalationRuleService
    {
        public Task<IReadOnlyList<TicketEscalationRule>> GetByWorkflowProfileIdAsync(Guid workflowProfileId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TicketEscalationRule>>(
                rules.Where(r => r.WorkflowProfileId == workflowProfileId).ToList());

        public Task<IReadOnlyList<TicketEscalationRule>> GetAllActiveAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TicketEscalationRule>>(rules.Where(r => r.IsActive).ToList());

        public Task<TicketEscalationRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<TicketEscalationRule?>(rules.FirstOrDefault(r => r.Id == id));

        public Task<TicketEscalationRule> CreateAsync(TicketEscalationRule rule, CancellationToken ct = default)
            => Task.FromResult(rule);

        public Task UpdateAsync(TicketEscalationRule rule, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
