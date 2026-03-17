using System.Text.Json;
using System.Text.RegularExpressions;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Meduza.Infrastructure.Services;

public class AgentAutoLabelingService : IAgentAutoLabelingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private const string EnabledRulesCacheKey = "label-rules:enabled";
    private const int EnabledRulesCacheTtlSeconds = 300;

    private readonly MeduzaDbContext _db;
    private readonly IAgentRepository _agentRepository;
    private readonly IAgentHardwareRepository _hardwareRepository;
    private readonly IAgentSoftwareRepository _softwareRepository;
    private readonly IAgentLabelRuleRepository _ruleRepository;
    private readonly IRedisService _redisService;
    private readonly ILogger<AgentAutoLabelingService> _logger;

    private readonly record struct PreparedRule(
        Guid RuleId,
        string Label,
        AgentLabelApplyMode ApplyMode,
        AgentLabelRuleExpressionNodeDto Expression);

    public AgentAutoLabelingService(
        MeduzaDbContext db,
        IAgentRepository agentRepository,
        IAgentHardwareRepository hardwareRepository,
        IAgentSoftwareRepository softwareRepository,
        IAgentLabelRuleRepository ruleRepository,
        IRedisService redisService,
        ILogger<AgentAutoLabelingService> logger)
    {
        _db = db;
        _agentRepository = agentRepository;
        _hardwareRepository = hardwareRepository;
        _softwareRepository = softwareRepository;
        _ruleRepository = ruleRepository;
        _redisService = redisService;
        _logger = logger;
    }

    public async Task EvaluateAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken = default)
    {
        var rules = await PrepareEnabledRulesAsync(cancellationToken);
        if (rules.Count == 0)
            return;

        await EvaluateAgentWithRulesAsync(agentId, reason, rules, cancellationToken);
    }

    public async Task<bool> HasEnabledRulesAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var rules = await GetCachedEnabledRulesAsync();
        return rules.Count > 0;
    }

    public async Task ReprocessAllAgentsAsync(string reason, int batchSize = 200, CancellationToken cancellationToken = default)
    {
        var safeBatchSize = Math.Clamp(batchSize, 25, 1000);
        var rules = await PrepareEnabledRulesAsync(cancellationToken);
        if (rules.Count == 0)
            return;

        Guid? cursor = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var currentBatch = await _db.Agents
                .AsNoTracking()
                .Where(agent => !cursor.HasValue || agent.Id.CompareTo(cursor.Value) > 0)
                .OrderBy(agent => agent.Id)
                .Select(agent => agent.Id)
                .Take(safeBatchSize)
                .ToListAsync(cancellationToken);

            if (currentBatch.Count == 0)
                break;

            foreach (var agentId in currentBatch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EvaluateAgentWithRulesAsync(agentId, reason, rules, cancellationToken);
            }

            cursor = currentBatch[^1];
        }
    }

    public async Task<AgentLabelRuleDryRunResponse> DryRunAsync(AgentLabelRuleDryRunRequest request, CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepository.GetByIdAsync(request.AgentId);
        if (agent is null)
            throw new InvalidOperationException("Agent not found.");

        var hardware = await _hardwareRepository.GetByAgentIdAsync(request.AgentId);
        var software = (await _softwareRepository.GetCurrentByAgentIdAsync(request.AgentId)).ToList();
        var matched = EvaluateNode(request.Expression, agent, hardware, software);

        var automaticLabels = await _db.AgentLabels
            .AsNoTracking()
            .Where(label => label.AgentId == request.AgentId && label.SourceType == AgentLabelSourceType.Automatic)
            .Select(label => label.Label)
            .OrderBy(label => label)
            .ToListAsync(cancellationToken);

        var hasLabel = !string.IsNullOrWhiteSpace(request.Label)
            && automaticLabels.Contains(request.Label, StringComparer.OrdinalIgnoreCase);

        return new AgentLabelRuleDryRunResponse
        {
            AgentId = request.AgentId,
            Matched = matched,
            Label = request.Label,
            WouldAddLabel = matched && !string.IsNullOrWhiteSpace(request.Label) && !hasLabel,
            WouldRemoveLabel =
                !matched
                && request.ApplyMode == AgentLabelApplyMode.ApplyAndRemove
                && !string.IsNullOrWhiteSpace(request.Label)
                && hasLabel,
            CurrentAutomaticLabels = automaticLabels
        };
    }

    private async Task EvaluateAgentWithRulesAsync(
        Guid agentId,
        string reason,
        IReadOnlyList<PreparedRule> rules,
        CancellationToken cancellationToken)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent is null)
            return;

        var hardware = await _hardwareRepository.GetByAgentIdAsync(agentId);
        var software = (await _softwareRepository.GetCurrentByAgentIdAsync(agentId)).ToList();

        var ruleIds = rules.Select(rule => rule.RuleId).ToList();
        var existingMatches = await _db.AgentLabelRuleMatches
            .Where(match => match.AgentId == agentId && ruleIds.Contains(match.RuleId))
            .ToDictionaryAsync(match => match.RuleId, cancellationToken);

        var now = DateTime.UtcNow;
        var matchStateChanged = false;

        foreach (var rule in rules)
        {
            var matched = EvaluateNode(rule.Expression, agent, hardware, software);
            var hasExistingMatch = existingMatches.TryGetValue(rule.RuleId, out var existing);

            if (matched)
            {
                if (!hasExistingMatch)
                {
                    _db.AgentLabelRuleMatches.Add(new AgentLabelRuleMatch
                    {
                        Id = IdGenerator.NewId(),
                        RuleId = rule.RuleId,
                        AgentId = agentId,
                        Label = rule.Label,
                        MatchedAt = now,
                        LastEvaluatedAt = now
                    });
                    matchStateChanged = true;
                    continue;
                }

                if (!string.Equals(existing!.Label, rule.Label, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Label = rule.Label;
                    existing.LastEvaluatedAt = now;
                    matchStateChanged = true;
                }

                continue;
            }

            if (!hasExistingMatch)
                continue;

            if (rule.ApplyMode == AgentLabelApplyMode.ApplyAndRemove)
            {
                _db.AgentLabelRuleMatches.Remove(existing!);
                matchStateChanged = true;
            }
        }

        if (matchStateChanged)
            await _db.SaveChangesAsync(cancellationToken);

        var labelsChanged = await SyncEffectiveLabelsAsync(agentId, now, cancellationToken);
        if (labelsChanged)
            await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Agent auto-labeling evaluated for {AgentId}. Reason: {Reason}",
            agentId,
            reason);
    }

    private async Task<IReadOnlyList<PreparedRule>> PrepareEnabledRulesAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var rules = await GetCachedEnabledRulesAsync();
        if (rules.Count == 0)
            return [];

        var prepared = new List<PreparedRule>(rules.Count);
        foreach (var rule in rules)
        {
            var expression = TryDeserializeExpression(rule.ExpressionJson);
            if (expression is null)
            {
                _logger.LogWarning("Agent label rule {RuleId} has invalid expression and was skipped.", rule.Id);
                continue;
            }

            prepared.Add(new PreparedRule(rule.Id, rule.Label, rule.ApplyMode, expression));
        }

        return prepared;
    }

    private async Task<IReadOnlyList<AgentLabelRule>> GetCachedEnabledRulesAsync()
    {
        var cached = await _redisService.GetAsync(EnabledRulesCacheKey);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            try
            {
                var deserialized = JsonSerializer.Deserialize<List<AgentLabelRule>>(cached, JsonOptions);
                if (deserialized is not null)
                    return deserialized;
            }
            catch (JsonException)
            {
                await _redisService.DeleteAsync(EnabledRulesCacheKey);
            }
        }

        var rules = await _ruleRepository.GetEnabledAsync();
        if (rules.Count > 0)
        {
            var payload = JsonSerializer.Serialize(rules, JsonOptions);
            await _redisService.SetAsync(EnabledRulesCacheKey, payload, EnabledRulesCacheTtlSeconds);
        }

        return rules;
    }

    private static AgentLabelRuleExpressionNodeDto? TryDeserializeExpression(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentLabelRuleExpressionNodeDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> SyncEffectiveLabelsAsync(Guid agentId, DateTime now, CancellationToken cancellationToken)
    {
        var automaticLabelsFromMatches = await _db.AgentLabelRuleMatches
            .AsNoTracking()
            .Join(
                _db.AgentLabelRules.AsNoTracking().Where(rule => rule.IsEnabled),
                match => match.RuleId,
                rule => rule.Id,
                (match, _) => match)
            .Where(item => item.AgentId == agentId)
            .Select(item => item.Label)
            .Distinct()
            .ToListAsync(cancellationToken);

        var existingAutomaticLabels = await _db.AgentLabels
            .Where(label => label.AgentId == agentId && label.SourceType == AgentLabelSourceType.Automatic)
            .ToListAsync(cancellationToken);

        var existingSet = existingAutomaticLabels
            .Select(item => item.Label)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var label in automaticLabelsFromMatches)
        {
            if (existingSet.Contains(label))
                continue;

            _db.AgentLabels.Add(new AgentLabel
            {
                Id = IdGenerator.NewId(),
                AgentId = agentId,
                Label = label,
                SourceType = AgentLabelSourceType.Automatic,
                CreatedAt = now,
                UpdatedAt = now
            });
            changed = true;
        }

        var shouldKeep = automaticLabelsFromMatches
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var label in existingAutomaticLabels)
        {
            if (shouldKeep.Contains(label.Label))
                continue;

            _db.AgentLabels.Remove(label);
            changed = true;
        }

        return changed;
    }

    private static bool EvaluateNode(
        AgentLabelRuleExpressionNodeDto node,
        Agent agent,
        AgentHardwareInfo? hardware,
        IReadOnlyCollection<AgentInstalledSoftware> software)
    {
        if (node.NodeType == AgentLabelNodeType.Condition)
            return EvaluateCondition(node, agent, hardware, software);

        if (node.Children.Count == 0)
            return false;

        var logicalOperator = node.LogicalOperator ?? AgentLabelLogicalOperator.And;

        return logicalOperator == AgentLabelLogicalOperator.And
            ? node.Children.All(child => EvaluateNode(child, agent, hardware, software))
            : node.Children.Any(child => EvaluateNode(child, agent, hardware, software));
    }

    private static bool EvaluateCondition(
        AgentLabelRuleExpressionNodeDto node,
        Agent agent,
        AgentHardwareInfo? hardware,
        IReadOnlyCollection<AgentInstalledSoftware> software)
    {
        if (!node.Field.HasValue || !node.Operator.HasValue)
            return false;

        var op = node.Operator.Value;
        var expected = node.Value ?? string.Empty;

        return node.Field.Value switch
        {
            AgentLabelField.Hostname => EvaluateText(agent.Hostname, op, expected),
            AgentLabelField.DisplayName => EvaluateText(agent.DisplayName, op, expected),
            AgentLabelField.IpAddress => EvaluateText(agent.LastIpAddress, op, expected),
            AgentLabelField.OperatingSystem => EvaluateText(agent.OperatingSystem, op, expected),
            AgentLabelField.OsVersion => EvaluateText(agent.OsVersion, op, expected),
            AgentLabelField.Status => EvaluateText(agent.Status.ToString(), op, expected),
            AgentLabelField.SoftwareName => software.Any(item => EvaluateText(item.Name, op, expected)),
            AgentLabelField.SoftwarePublisher => software.Any(item => EvaluateText(item.Publisher, op, expected)),
            AgentLabelField.SoftwareVersion => software.Any(item => EvaluateText(item.Version, op, expected)),
            AgentLabelField.SoftwareCount => EvaluateNumber((int?)software.Count, op, expected),
            AgentLabelField.Processor => EvaluateText(hardware?.Processor, op, expected),
            AgentLabelField.TotalMemoryBytes => EvaluateNumber(hardware?.TotalMemoryBytes, op, expected),
            AgentLabelField.TotalDisksCount => EvaluateNumber(hardware?.TotalDisksCount, op, expected),
            _ => false
        };
    }

    private static bool EvaluateText(string? current, AgentLabelComparisonOperator op, string expected)
    {
        if (current is null)
            return false;

        return op switch
        {
            AgentLabelComparisonOperator.Contains => current.Contains(expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.NotContains => !current.Contains(expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.StartsWith => current.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.EndsWith => current.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.Equals => string.Equals(current, expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.NotEquals => !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase),
            AgentLabelComparisonOperator.Regex => EvaluateRegex(current, expected),
            _ => false
        };
    }

    private static bool EvaluateRegex(string current, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        try
        {
            return Regex.IsMatch(current, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool EvaluateNumber(long? current, AgentLabelComparisonOperator op, string expected)
    {
        if (!current.HasValue)
            return false;

        return EvaluateNumber((decimal)current.Value, op, expected);
    }

    private static bool EvaluateNumber(int? current, AgentLabelComparisonOperator op, string expected)
    {
        if (!current.HasValue)
            return false;

        return EvaluateNumber((decimal)current.Value, op, expected);
    }

    private static bool EvaluateNumber(decimal current, AgentLabelComparisonOperator op, string expected)
    {
        if (!decimal.TryParse(expected, out var expectedValue))
            return false;

        return op switch
        {
            AgentLabelComparisonOperator.Equals => current == expectedValue,
            AgentLabelComparisonOperator.NotEquals => current != expectedValue,
            AgentLabelComparisonOperator.GreaterThan => current > expectedValue,
            AgentLabelComparisonOperator.GreaterThanOrEqual => current >= expectedValue,
            AgentLabelComparisonOperator.LessThan => current < expectedValue,
            AgentLabelComparisonOperator.LessThanOrEqual => current <= expectedValue,
            _ => false
        };
    }
}
