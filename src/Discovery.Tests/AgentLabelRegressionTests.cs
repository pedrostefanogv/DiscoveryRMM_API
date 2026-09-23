using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentLabels;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Testes de regressao para os bugs corrigidos no auto-labeling:
///   1. Cache "label-rules:enabled" nao era invalidado nos writes das regras.
///   2. AgentLabelExpressionValidator existia mas nunca era chamado em producao.
///   3. Campos de disco fora de um DiskGroup passavam na validacao.
///   4. ApplyMode invalido caia silenciosamente em ApplyAndRemove.
///   5. Label manual nao era normalizada (trim) nem validada em tamanho.
///   6. Excluir label inexistente respondia sucesso em vez de 404.
/// </summary>
public class AgentLabelRegressionTests
{
    private static readonly Guid RuleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AgentId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // -------------------------------------------------------------------------
    // 1. Invalidacao de cache
    // -------------------------------------------------------------------------

    [Test]
    public async Task CreateRule_InvalidatesEnabledRulesCache()
    {
        var redis = new RecordingRedisService();
        var service = BuildLabelService(redis);

        await service.CreateRuleAsync(new AgentLabelRule { Name = "r", Label = "L" });

        Assert.That(redis.DeletedKeys, Does.Contain(AgentLabelingCacheKeys.EnabledRules),
            "Criar regra deve invalidar o cache de regras habilitadas.");
    }

    [Test]
    public async Task UpdateRule_InvalidatesEnabledRulesCache()
    {
        var redis = new RecordingRedisService();
        var service = BuildLabelService(redis);

        await service.UpdateRuleAsync(new AgentLabelRule { Id = RuleId, Name = "r", Label = "L" });

        Assert.That(redis.DeletedKeys, Does.Contain(AgentLabelingCacheKeys.EnabledRules),
            "Atualizar (ex.: desabilitar) regra deve invalidar o cache.");
    }

    [Test]
    public async Task DeleteRule_InvalidatesEnabledRulesCache()
    {
        var redis = new RecordingRedisService();
        var service = BuildLabelService(redis);

        await service.DeleteRuleAsync(RuleId);

        Assert.That(redis.DeletedKeys, Does.Contain(AgentLabelingCacheKeys.EnabledRules),
            "Excluir regra deve invalidar o cache.");
    }

    [Test]
    public async Task CreateRule_WhenCacheFails_StillPersists()
    {
        var redis = new RecordingRedisService { ThrowOnDelete = true };
        var service = BuildLabelService(redis);

        // Falha de cache nunca deve derrubar a escrita.
        var created = await service.CreateRuleAsync(new AgentLabelRule { Name = "r", Label = "L" });

        Assert.That(created, Is.Not.Null);
        Assert.That(created.Label, Is.EqualTo("L"));
    }

    // -------------------------------------------------------------------------
    // 2/3/4. Validacao server-side das regras
    // -------------------------------------------------------------------------

    [Test]
    public async Task CreateRule_WithInvalidApplyMode_Fails()
    {
        var handler = new CreateLabelRuleCommandHandler(new StubLabelService(), new StubCustomFieldService());

        var result = await handler.Handle(
            new CreateLabelRuleCommand("r", "L", null, true, "ManualX", ValidTextExpression(), null),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("applyMode"));
    }

    [Test]
    public async Task CreateRule_WithMalformedJson_Fails()
    {
        var handler = new CreateLabelRuleCommandHandler(new StubLabelService(), new StubCustomFieldService());

        var result = await handler.Handle(
            new CreateLabelRuleCommand("r", "L", null, true, "ApplyOnly", "{ nao-e-json", null),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("expression"));
    }

    [Test]
    public async Task CreateRule_WithDiskFieldOutsideDiskGroup_Fails()
    {
        // Antes essa regra era aceita e silenciosamente nunca dava match.
        var expression = """
        {"nodeType":0,"logicalOperator":0,"children":[
          {"nodeType":1,"field":17,"operator":4,"value":"C:"}
        ]}
        """;

        var handler = new CreateLabelRuleCommandHandler(new StubLabelService(), new StubCustomFieldService());
        var result = await handler.Handle(
            new CreateLabelRuleCommand("r", "L", null, true, "ApplyOnly", expression, null),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Message, Does.Contain("DiskGroup"));
    }

    [Test]
    public async Task CreateRule_WithValidDiskGroupExpression_Succeeds()
    {
        var expression = """
        {"nodeType":2,"logicalOperator":0,"children":[
          {"nodeType":1,"field":17,"operator":4,"value":"C:"}
        ]}
        """;

        var svc = new StubLabelService();
        var handler = new CreateLabelRuleCommandHandler(svc, new StubCustomFieldService());
        var result = await handler.Handle(
            new CreateLabelRuleCommand("PC", "SSD", null, true, "ApplyOnly", expression, null),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.That(result.Value!.Label, Is.EqualTo("SSD"));
    }

    [Test]
    public void Validator_RejectsDiskFieldOutsideDiskGroup_ButAcceptsInside()
    {
        var outside = new AgentLabelRuleExpressionNodeDto
        {
            NodeType = AgentLabelNodeType.Group,
            LogicalOperator = AgentLabelLogicalOperator.And,
            Children =
            [
                new AgentLabelRuleExpressionNodeDto
                {
                    NodeType = AgentLabelNodeType.Condition,
                    Field = AgentLabelField.DiskFreeSpacePercent,
                    Operator = AgentLabelComparisonOperator.LessThan,
                    Value = "20"
                }
            ]
        };

        Assert.That(AgentLabelExpressionValidator.Validate(outside), Is.Not.Empty);

        outside.Children[0].NodeType = AgentLabelNodeType.Condition;
        var inside = new AgentLabelRuleExpressionNodeDto
        {
            NodeType = AgentLabelNodeType.DiskGroup,
            LogicalOperator = AgentLabelLogicalOperator.Or,
            Children = outside.Children
        };

        Assert.That(AgentLabelExpressionValidator.Validate(inside), Is.Empty);
    }

    [Test]
    public async Task CreateRule_WithOversizedName_Fails()
    {
        var handler = new CreateLabelRuleCommandHandler(new StubLabelService(), new StubCustomFieldService());

        var result = await handler.Handle(
            new CreateLabelRuleCommand(new string('n', 201), "L", null, true, "ApplyOnly", ValidTextExpression(), null),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("name"));
    }

    // -------------------------------------------------------------------------
    // 5. Label manual: trim + tamanho
    // -------------------------------------------------------------------------

    [Test]
    public async Task AddManualLabel_TrimsWhitespace()
    {
        var svc = new StubLabelService();
        var handler = new AddAgentLabelCommandHandler(svc);

        var result = await handler.Handle(new AddAgentLabelCommand(AgentId, "  PROD  "), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Label, Is.EqualTo("PROD"),
            "Espacos nas bordas criavam uma label distinta de 'PROD'.");
    }

    [Test]
    public async Task AddManualLabel_WhenTooLong_Fails()
    {
        var handler = new AddAgentLabelCommandHandler(new StubLabelService());

        var result = await handler.Handle(
            new AddAgentLabelCommand(AgentId, new string('x', 121)), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("label"));
    }

    [Test]
    public async Task AddManualLabel_WhenWhitespaceOnly_Fails()
    {
        var handler = new AddAgentLabelCommandHandler(new StubLabelService());

        var result = await handler.Handle(new AddAgentLabelCommand(AgentId, "   "), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
    }

    // -------------------------------------------------------------------------
    // 6. Delete de label inexistente => NotFound
    // -------------------------------------------------------------------------

    [Test]
    public void DeleteLabelCommand_WhenMissing_ReturnsNotFound()
    {
        var svc = new StubLabelService { DeleteResult = false };
        var handler = new RemoveAgentLabelCommandHandler(svc);

        var result = handler.Handle(new RemoveAgentLabelCommand(Guid.NewGuid()), CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string ValidTextExpression() => """
    {"nodeType":0,"logicalOperator":0,"children":[
      {"nodeType":1,"field":0,"operator":0,"value":"SRV"}
    ]}
    """;

    private static LabelService BuildLabelService(RecordingRedisService redis) => new(
        new StubAgentLabelRepository(),
        new StubAgentLabelRuleRepository(),
        redis,
        NullLogger<LabelService>.Instance);

    private sealed class RecordingRedisService : IRedisService
    {
        public List<string> DeletedKeys { get; } = [];
        public bool ThrowOnDelete { get; init; }

        public bool IsConnected => true;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.CompletedTask;
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(true);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(-1);
        public Task DeleteAsync(string key)
        {
            DeletedKeys.Add(key);
            return ThrowOnDelete ? Task.FromException(new InvalidOperationException("redis fora do ar")) : Task.CompletedTask;
        }
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }

    private sealed class StubAgentLabelRepository : IAgentLabelRepository
    {
        public Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId) => Task.FromResult<IReadOnlyList<AgentLabel>>([]);
        public Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds) => Task.FromResult<IReadOnlyList<AgentLabel>>([]);
        public Task<IReadOnlyList<AgentLabelRuleAgentResponse>> GetAgentsByRuleIdAsync(Guid ruleId) => Task.FromResult<IReadOnlyList<AgentLabelRuleAgentResponse>>([]);
        public Task<(int Total, IReadOnlyList<AgentLabelRuleAgentResponse> Agents)> GetAgentsByRuleIdPagedAsync(Guid ruleId, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult((0, (IReadOnlyList<AgentLabelRuleAgentResponse>)[]));
        public Task<IReadOnlyList<string>> GetDistinctLabelsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<AgentLabel?> GetByIdAsync(Guid id) => Task.FromResult<AgentLabel?>(null);
        public Task<AgentLabel> AddAsync(AgentLabel label) => Task.FromResult(label);
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task SuppressAutomaticLabelAsync(Guid agentId, string label, string? suppressedBy, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearSuppressionAsync(Guid agentId, string label, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentLabelSuppressionDto>> GetSuppressionsByAgentIdAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelSuppressionDto>>([]);
        public Task<bool> ReleaseSuppressionAsync(Guid suppressionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<Guid>> GetAgentIdsByLabelPagedAsync(string label, Guid? afterAgentId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<int> CountAgentsByLabelAsync(string label, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<AgentLabelUsageDto>> GetLabelUsageAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelUsageDto>>([]);
    }

    private sealed class StubAgentLabelRuleRepository : IAgentLabelRuleRepository
    {
        public Task<IReadOnlyList<AgentLabelRule>> GetAllAsync(bool includeDisabled = true) => Task.FromResult<IReadOnlyList<AgentLabelRule>>([]);
        public Task<IReadOnlyList<AgentLabelRule>> GetEnabledAsync() => Task.FromResult<IReadOnlyList<AgentLabelRule>>([]);
        public Task<AgentLabelRule?> GetByIdAsync(Guid id) => Task.FromResult<AgentLabelRule?>(null);
        public Task<AgentLabelRule> CreateAsync(AgentLabelRule rule) => Task.FromResult(rule);
        public Task UpdateAsync(AgentLabelRule rule) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    private sealed class StubLabelService : ILabelService
    {
        public bool DeleteResult { get; init; } = true;
        public AgentLabelRule? Created { get; private set; }

        public Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabel>>([]);
        public Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabel>>([]);
        public Task<IReadOnlyList<string>> GetDistinctLabelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<AgentLabel?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<AgentLabel?>(null);
        public Task<AgentLabel> AddAsync(AgentLabel label, CancellationToken ct = default)
            => Task.FromResult(label);
        public Task<AgentLabel> AddWithSuppressionClearAsync(AgentLabel label, CancellationToken ct = default)
            => Task.FromResult(label);
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(DeleteResult);
        public Task<bool> DeleteWithSuppressionAsync(Guid id, string? suppressedBy, CancellationToken ct = default) => Task.FromResult(DeleteResult);
        public Task<IReadOnlyList<Guid>> GetAgentIdsByLabelPagedAsync(string label, Guid? afterAgentId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<int> CountAgentsByLabelAsync(string label, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<AgentLabelUsageDto>> GetLabelUsageAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelUsageDto>>([]);
        public Task<IReadOnlyList<AgentLabelSuppressionDto>> GetSuppressionsByAgentIdAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelSuppressionDto>>([]);
        public Task<bool> ReleaseSuppressionAsync(Guid suppressionId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<IReadOnlyList<AgentLabelRule>> GetRulesAsync(bool includeDisabled = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelRule>>([]);
        public Task<AgentLabelRule?> GetRuleByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<AgentLabelRule?>(null);
        public Task<AgentLabelRule> CreateRuleAsync(AgentLabelRule rule, CancellationToken ct = default)
        {
            Created = rule;
            return Task.FromResult(rule);
        }
        public Task UpdateRuleAsync(AgentLabelRule rule, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteRuleAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentLabelRuleAgentResponse>> GetAgentsByRuleIdAsync(Guid ruleId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelRuleAgentResponse>>([]);
        public Task<(int Total, IReadOnlyList<AgentLabelRuleAgentResponse> Agents)> GetAgentsByRuleIdPagedAsync(Guid ruleId, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult((0, (IReadOnlyList<AgentLabelRuleAgentResponse>)[]));
    }

    private sealed class StubCustomFieldService : ICustomFieldService
    {
        public Task<IReadOnlyList<CustomFieldDefinition>> GetDefinitionsAsync(
            CustomFieldScopeType? scopeType, bool includeInactive = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CustomFieldDefinition>>([]);

        public Task<CustomFieldDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<CustomFieldDefinition?>(null);

        public Task<CustomFieldDefinition> CreateDefinitionAsync(UpsertCustomFieldDefinitionInput input, string? updatedBy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomFieldDefinition?> UpdateDefinitionAsync(Guid id, UpsertCustomFieldDefinitionInput input, string? updatedBy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeactivateDefinitionAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomFieldResolvedValueDto>> GetValuesAsync(
            CustomFieldScopeType scopeType, Guid? entityId, bool includeSecrets = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CursorPageDto<CustomFieldResolvedValueDto>> GetValuesPageAsync(
            CustomFieldScopeType scopeType, Guid? entityId, string? cursor, int limit, bool includeSecrets = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomFieldSchemaItemDto>> GetSchemaAsync(
            CustomFieldScopeType scopeType, Guid? entityId, bool includeInactive = false, bool includeSecrets = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomFieldResolvedValueDto> UpsertValueAsync(UpsertCustomFieldValueInput input, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeCustomFieldDto>> GetRuntimeValuesForAgentAsync(Guid agentId, Guid? taskId, Guid? scriptId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomFieldResolvedValueDto> UpsertAgentCollectedValueAsync(Guid agentId, AgentCustomFieldCollectedValueInput input, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
