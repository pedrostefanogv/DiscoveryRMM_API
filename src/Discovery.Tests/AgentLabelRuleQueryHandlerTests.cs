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

    /// <summary>Expressao estruturalmente valida (o dry-run passou a validar a arvore).</summary>
    private static AgentLabelRuleExpressionNodeDto ValidExpression() => new()
    {
        NodeType = AgentLabelNodeType.Group,
        LogicalOperator = AgentLabelLogicalOperator.And,
        Children =
        [
            new AgentLabelRuleExpressionNodeDto
            {
                NodeType = AgentLabelNodeType.Condition,
                Field = AgentLabelField.Hostname,
                Operator = AgentLabelComparisonOperator.Contains,
                Value = "SRV"
            }
        ]
    };

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
            new DryRunLabelRuleQuery(new AgentLabelRuleDryRunRequest { AgentId = AgentId, Expression = ValidExpression() }),
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
            new DryRunLabelRuleQuery(new AgentLabelRuleDryRunRequest
            {
                AgentId = AgentId,
                Label = "Windows",
                Expression = ValidExpression()
            }),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.Not.Null);
        Assert.That(result.Value!.AgentId, Is.EqualTo(AgentId));
    }

    [Test]
    public async Task DryRun_WhenExpressionInvalid_ReturnsValidationError()
    {
        var handler = new DryRunLabelRuleQueryHandler(new FakeAutoLabelingService());

        // Grupo vazio: o dry-run agora valida a arvore antes de tocar o motor.
        var result = await handler.Handle(
            new DryRunLabelRuleQuery(new AgentLabelRuleDryRunRequest { AgentId = AgentId }),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("expression"));
    }

    // -------------------------------------------------------------------------
    // DryRunLabelRuleBatchQueryHandler
    // -------------------------------------------------------------------------

    [Test]
    public async Task DryRunBatch_WithoutAgents_ReturnsValidationError()
    {
        var handler = new DryRunLabelRuleBatchQueryHandler(new FakeAutoLabelingService());

        var result = await handler.Handle(
            new DryRunLabelRuleBatchQuery(new AgentLabelRuleDryRunBatchRequest { Expression = ValidExpression() }),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("agentIds"));
    }

    [Test]
    public async Task DryRunBatch_WithMoreThanLimit_ReturnsValidationError()
    {
        var handler = new DryRunLabelRuleBatchQueryHandler(new FakeAutoLabelingService());
        var agentIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList();

        var result = await handler.Handle(
            new DryRunLabelRuleBatchQuery(new AgentLabelRuleDryRunBatchRequest { AgentIds = agentIds, Expression = ValidExpression() }),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("agentIds"));
    }

    [Test]
    public async Task DryRunBatch_WithValidAgents_ReturnsSuccess()
    {
        var handler = new DryRunLabelRuleBatchQueryHandler(new FakeAutoLabelingService());

        var result = await handler.Handle(
            new DryRunLabelRuleBatchQuery(new AgentLabelRuleDryRunBatchRequest { AgentIds = [AgentId], Expression = ValidExpression() }),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
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

        public Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabel>>([]);

        public Task<IReadOnlyList<string>> GetDistinctLabelsAsync(int limit, AgentLabelSourceType? sourceType, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<AgentLabel?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<AgentLabel?>(null);

        public Task<AgentLabel> AddAsync(AgentLabel label, CancellationToken ct = default)
            => Task.FromResult(label);

        public Task<AgentLabel> AddWithSuppressionClearAsync(AgentLabel label, CancellationToken ct = default)
            => Task.FromResult(label);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> DeleteWithSuppressionAsync(Guid id, string? suppressedBy, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> GetAgentIdsByLabelPagedAsync(string label, Guid? afterAgentId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<int> CountAgentsByLabelAsync(string label, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<IReadOnlyList<AgentLabelUsageDto>> GetLabelUsageAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelUsageDto>>([]);

        public Task<IReadOnlyList<AgentLabelSuppressionDto>> GetSuppressionsByAgentIdAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelSuppressionDto>>([]);

        public Task<Guid?> ReleaseSuppressionAsync(Guid suppressionId, CancellationToken ct = default)
            => Task.FromResult<Guid?>(AgentId);

        public Task<IReadOnlyList<AgentLabelChangeLogDto>> GetChangeLogAsync(Guid agentId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelChangeLogDto>>([]);

        public Task<IReadOnlyList<AgentLabelRule>> GetRulesAsync(bool includeDisabled = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelRule>>([]);

        public Task<AgentLabelRule?> GetRuleByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_rule);

        public Task<AgentLabelRule> CreateRuleAsync(AgentLabelRule rule, CancellationToken ct = default)
            => Task.FromResult(rule);

        public Task UpdateRuleAsync(AgentLabelRule rule, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ImportRulesAsync(IReadOnlyList<AgentLabelRule> rules, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<LabelRuleVersionDto>> GetRuleVersionsAsync(Guid ruleId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LabelRuleVersionDto>>([]);

        public Task<IReadOnlyList<AgentLabelProtectedLabelDto>> GetProtectedLabelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelProtectedLabelDto>>([]);

        public Task<AgentLabelProtectedLabelDto> AddProtectedLabelAsync(string label, string? createdBy, CancellationToken ct = default)
            => Task.FromResult(new AgentLabelProtectedLabelDto
            {
                Id = Guid.NewGuid(),
                Label = label,
                CreatedBy = createdBy,
                CreatedAt = DateTime.UtcNow
            });

        public Task<bool> RemoveProtectedLabelAsync(Guid id, CancellationToken ct = default) => Task.FromResult(true);

        public Task DeleteRuleAsync(Guid id, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AgentLabelRuleAgentResponse>> GetAgentsByRuleIdAsync(Guid ruleId, CancellationToken ct = default)
            => Task.FromResult(_agentsByRule);

        public Task<(int Total, IReadOnlyList<AgentLabelRuleAgentResponse> Agents)> GetAgentsByRuleIdPagedAsync(
            Guid ruleId, int page, int pageSize, CancellationToken ct = default)
        {
            var safePage = page < 1 ? 1 : page;
            var safePageSize = pageSize < 1 ? 1 : pageSize;
            var slice = _agentsByRule
                .Skip((safePage - 1) * safePageSize)
                .Take(safePageSize)
                .ToList();

            return Task.FromResult<(int, IReadOnlyList<AgentLabelRuleAgentResponse>)>(
                (_agentsByRule.Count, slice));
        }
    }

    private sealed class FakeAutoLabelingService : IAgentAutoLabelingService
    {
        private readonly bool _throwAgentNotFound;

        public FakeAutoLabelingService(bool throwAgentNotFound = false)
        {
            _throwAgentNotFound = throwAgentNotFound;
        }

        public Task EvaluateAgentAsync(Guid agentId, string reason, string? actor = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EvaluateAgentsAsync(IReadOnlyCollection<Guid> agentIds, string reason, string? actor = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> HasEnabledRulesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task ReprocessAllAgentsAsync(string reason, int batchSize = 200, string? actor = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReprocessAllAgentsAsync(string reason, int batchSize, IProgress<AgentLabelReprocessProgress>? progress, string? actor = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReprocessChangedAgentsAsync(string reason, int batchSize = 200, IProgress<AgentLabelReprocessProgress>? progress = null, string? actor = null, CancellationToken cancellationToken = default)
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

        public Task<IReadOnlyList<AgentLabelRuleDryRunResponse>> DryRunBatchAsync(AgentLabelRuleDryRunBatchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentLabelRuleDryRunResponse>>([]);

        public Task<AgentLabelRuleImpactResponse> EvaluateImpactAsync(AgentLabelRuleImpactRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentLabelRuleImpactResponse());
    }
}
