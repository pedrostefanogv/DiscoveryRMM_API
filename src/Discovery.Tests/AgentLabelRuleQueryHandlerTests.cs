using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentLabels;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Tests for the label-rule query handlers that back the "agents by rule" and
/// "dry-run preview" endpoints:
///   - ListAgentsByRuleQueryHandler
///   - DryRunLabelRuleQueryHandler
/// </summary>
public class AgentLabelRuleQueryHandlerTests
{
    private static readonly Guid RuleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // -------------------------------------------------------------------------
    // ListAgentsByRuleQueryHandler
    // -------------------------------------------------------------------------

    [Test]
    public async Task ListAgentsByRule_WhenRuleExists_ReturnsMatchedAgents()
    {
        var rule = new AgentLabelRule { Id = RuleId, Name = "Windows", Label = "Windows" };
        var agents = new List<AgentLabelRuleAgentResponse>
        {
            new() { AgentId = AgentId, Hostname = "host-01", Status = AgentStatus.Online }
        };

        var svc = new FakeLabelService(rule: rule, agentsByRule: agents);
        var handler = new ListAgentsByRuleQueryHandler(svc);

        var result = await handler.Handle(new ListAgentsByRuleQuery(RuleId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.Not.Null);
        Assert.That(result.Value!.RuleId, Is.EqualTo(RuleId));
        Assert.That(result.Value.RuleName, Is.EqualTo("Windows"));
        Assert.That(result.Value.TotalAgents, Is.EqualTo(1));
        Assert.That(result.Value.Agents, Is.Not.Null);
        Assert.That(result.Value.Agents.Count, Is.EqualTo(1));
        Assert.That(result.Value.Agents[0].Hostname, Is.EqualTo("host-01"));
    }

    [Test]
    public async Task ListAgentsByRule_WhenRuleMissing_ReturnsNotFound()
    {
        var svc = new FakeLabelService(rule: null, agentsByRule: []);
        var handler = new ListAgentsByRuleQueryHandler(svc);

        var result = await handler.Handle(new ListAgentsByRuleQuery(RuleId), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    // -------------------------------------------------------------------------
    // DryRunLabelRuleQueryHandler
    // -------------------------------------------------------------------------

    [Test]
    public async Task DryRun_WhenAgentIdEmpty_ReturnsValidationError()
    {
        var auto = new FakeAutoLabelingService();
        var handler = new DryRunLabelRuleQueryHandler(auto);

        var result = await handler.Handle(
            new DryRunLabelRuleQuery(new AgentLabelRuleDryRunRequest { AgentId = Guid.Empty }),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task DryRun_WhenAgentNotFound_ReturnsNotFound()
    {
        var auto = new FakeAutoLabelingService(throwAgentNotFound: true);
        var handler = new DryRunLabelRuleQueryHandler(auto);

        var result = await handler.Handle(
            new DryRunLabelRuleQuery(new AgentLabelRuleDryRunRequest { AgentId = AgentId }),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task DryRun_WhenAgentExists_ReturnsDryRunResponse()
    {
        var auto = new FakeAutoLabelingService();
        var handler = new DryRunLabelRuleQueryHandler(auto);

        var result = await handler.Handle(
            new DryRunLabelRuleQuery(new AgentLabelRuleDryRunRequest { AgentId = AgentId, Label = "Windows" }),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.Not.Null);
        Assert.That(result.Value!.AgentId, Is.EqualTo(AgentId));
    }

    // -------------------------------------------------------------------------
    // Fakes
    // -------------------------------------------------------------------------

    private sealed class FakeLabelService : ILabelService
    {
        private readonly AgentLabelRule? _rule;
        private readonly IReadOnlyList<AgentLabelRuleAgentResponse> _agentsByRule;

        public FakeLabelService(AgentLabelRule? rule, IReadOnlyList<AgentLabelRuleAgentResponse> agentsByRule)
        {
            _rule = rule;
            _agentsByRule = agentsByRule;
        }

        public Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabel>>([]);

        public Task<IReadOnlyList<string>> GetDistinctLabelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<AgentLabel?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<AgentLabel?>(null);

        public Task<AgentLabel> AddAsync(AgentLabel label, CancellationToken ct = default)
            => Task.FromResult(label);

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AgentLabelRule>> GetRulesAsync(bool includeDisabled = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelRule>>([]);

        public Task<AgentLabelRule?> GetRuleByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_rule);

        public Task<AgentLabelRule> CreateRuleAsync(AgentLabelRule rule, CancellationToken ct = default)
            => Task.FromResult(rule);

        public Task UpdateRuleAsync(AgentLabelRule rule, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteRuleAsync(Guid id, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AgentLabelRuleAgentResponse>> GetAgentsByRuleIdAsync(Guid ruleId, CancellationToken ct = default)
            => Task.FromResult(_agentsByRule);
    }

    private sealed class FakeAutoLabelingService : IAgentAutoLabelingService
    {
        private readonly bool _throwAgentNotFound;

        public FakeAutoLabelingService(bool throwAgentNotFound = false)
        {
            _throwAgentNotFound = throwAgentNotFound;
        }

        public Task EvaluateAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> HasEnabledRulesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task ReprocessAllAgentsAsync(string reason, int batchSize = 200, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AgentLabelRuleDryRunResponse> DryRunAsync(AgentLabelRuleDryRunRequest request, CancellationToken cancellationToken = default)
        {
            if (_throwAgentNotFound)
                throw new InvalidOperationException("Agent not found.");

            return Task.FromResult(new AgentLabelRuleDryRunResponse
            {
                AgentId = request.AgentId,
                Matched = true,
                Label = request.Label,
                WouldAddLabel = true,
                WouldRemoveLabel = false,
                CurrentAutomaticLabels = []
            });
        }
    }
}
