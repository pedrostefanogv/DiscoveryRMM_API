using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AgentAutoLabelingService : IAgentAutoLabelingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private const string EnabledRulesCacheKey = "label-rules:enabled";
    private const int EnabledRulesCacheTtlSeconds = 300;

    private readonly DiscoveryDbContext _db;
    private readonly IAgentRepository _agentRepository;
    private readonly IAgentHardwareRepository _hardwareRepository;
    private readonly IAgentSoftwareRepository _softwareRepository;
    private readonly IAgentLabelRuleRepository _ruleRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IRedisService _redisService;
    private readonly ILogger<AgentAutoLabelingService> _logger;

    private readonly record struct PreparedRule(
        Guid RuleId,
        string Label,
        AgentLabelApplyMode ApplyMode,
        AgentLabelRuleExpressionNodeDto Expression);

    private readonly record struct CustomFieldEntry(string ValueJson, CustomFieldDataType DataType);

    public AgentAutoLabelingService(
        DiscoveryDbContext db,
        IAgentRepository agentRepository,
        IAgentHardwareRepository hardwareRepository,
        IAgentSoftwareRepository softwareRepository,
        IAgentLabelRuleRepository ruleRepository,
        ISiteRepository siteRepository,
        IRedisService redisService,
        ILogger<AgentAutoLabelingService> logger)
    {
        _db = db;
        _agentRepository = agentRepository;
        _hardwareRepository = hardwareRepository;
        _softwareRepository = softwareRepository;
        _ruleRepository = ruleRepository;
        _siteRepository = siteRepository;
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

        var customFieldValues = HasCustomFieldConditions(request.Expression)
            ? await LoadCustomFieldValuesForAgentAsync(request.AgentId, agent.SiteId, cancellationToken)
            : null;

        var matched = EvaluateNode(request.Expression, agent, hardware, software, customFieldValues);

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

        var needsCustomFields = rules.Any(rule => HasCustomFieldConditions(rule.Expression));
        var customFieldValues = needsCustomFields
            ? await LoadCustomFieldValuesForAgentAsync(agentId, agent.SiteId, cancellationToken)
            : null;

        var ruleIds = rules.Select(rule => rule.RuleId).ToList();
        var existingMatches = await _db.AgentLabelRuleMatches
            .Where(match => match.AgentId == agentId && ruleIds.Contains(match.RuleId))
            .ToDictionaryAsync(match => match.RuleId, cancellationToken);

        var now = DateTime.UtcNow;
        var matchStateChanged = false;

        foreach (var rule in rules)
        {
            var matched = EvaluateNode(rule.Expression, agent, hardware, software, customFieldValues);
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
        IReadOnlyCollection<AgentInstalledSoftware> software,
        IReadOnlyDictionary<Guid, CustomFieldEntry>? customFieldValues)
    {
        if (node.NodeType == AgentLabelNodeType.Condition)
            return EvaluateCondition(node, agent, hardware, software, customFieldValues);

        if (node.Children.Count == 0)
            return false;

        var logicalOperator = node.LogicalOperator ?? AgentLabelLogicalOperator.And;

        return logicalOperator == AgentLabelLogicalOperator.And
            ? node.Children.All(child => EvaluateNode(child, agent, hardware, software, customFieldValues))
            : node.Children.Any(child => EvaluateNode(child, agent, hardware, software, customFieldValues));
    }

    private static bool EvaluateCondition(
        AgentLabelRuleExpressionNodeDto node,
        Agent agent,
        AgentHardwareInfo? hardware,
        IReadOnlyCollection<AgentInstalledSoftware> software,
        IReadOnlyDictionary<Guid, CustomFieldEntry>? customFieldValues)
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
            AgentLabelField.AgentCustomField
                or AgentLabelField.ClientCustomField
                or AgentLabelField.SiteCustomField
                => EvaluateCustomField(node.CustomFieldDefinitionId, op, expected, customFieldValues),
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

    private static bool EvaluateCustomField(
        Guid? definitionId,
        AgentLabelComparisonOperator op,
        string expected,
        IReadOnlyDictionary<Guid, CustomFieldEntry>? customFieldValues)
    {
        if (!definitionId.HasValue || customFieldValues is null)
            return false;

        if (!customFieldValues.TryGetValue(definitionId.Value, out var entry))
            return false;

        return entry.DataType switch
        {
            CustomFieldDataType.Integer or CustomFieldDataType.Decimal =>
                EvaluateCustomFieldNumeric(entry.ValueJson, op, expected),
            CustomFieldDataType.Boolean =>
                EvaluateCustomFieldBoolean(entry.ValueJson, op, expected),
            CustomFieldDataType.Date or CustomFieldDataType.DateTime =>
                EvaluateCustomFieldDateTime(entry.ValueJson, op, expected),
            _ => // Text, Dropdown, ListBox
                EvaluateCustomFieldText(entry.ValueJson, op, expected)
        };
    }

    private static bool EvaluateCustomFieldText(string valueJson, AgentLabelComparisonOperator op, string expected)
    {
        try
        {
            var text = JsonSerializer.Deserialize<string?>(valueJson, JsonOptions);
            return EvaluateText(text, op, expected);
        }
        catch (JsonException) { return false; }
    }

    private static bool EvaluateCustomFieldNumeric(string valueJson, AgentLabelComparisonOperator op, string expected)
    {
        try
        {
            var numeric = JsonSerializer.Deserialize<decimal?>(valueJson, JsonOptions);
            if (!numeric.HasValue) return false;
            return EvaluateNumber(numeric.Value, op, expected);
        }
        catch (JsonException) { return false; }
    }

    private static bool EvaluateCustomFieldBoolean(string valueJson, AgentLabelComparisonOperator op, string expected)
    {
        try
        {
            var boolValue = JsonSerializer.Deserialize<bool?>(valueJson, JsonOptions);
            if (!boolValue.HasValue) return false;
            var current = boolValue.Value ? "true" : "false";
            return op switch
            {
                AgentLabelComparisonOperator.Equals =>
                    string.Equals(current, expected, StringComparison.OrdinalIgnoreCase),
                AgentLabelComparisonOperator.NotEquals =>
                    !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }
        catch (JsonException) { return false; }
    }

    private static bool EvaluateCustomFieldDateTime(string valueJson, AgentLabelComparisonOperator op, string expected)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<DateTimeOffset?>(valueJson, JsonOptions);
            if (!stored.HasValue) return false;
            if (!DateTimeOffset.TryParse(expected, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expectedDate))
                return false;
            var diff = stored.Value.CompareTo(expectedDate);
            return op switch
            {
                AgentLabelComparisonOperator.Equals => diff == 0,
                AgentLabelComparisonOperator.NotEquals => diff != 0,
                AgentLabelComparisonOperator.GreaterThan => diff > 0,
                AgentLabelComparisonOperator.GreaterThanOrEqual => diff >= 0,
                AgentLabelComparisonOperator.LessThan => diff < 0,
                AgentLabelComparisonOperator.LessThanOrEqual => diff <= 0,
                _ => false
            };
        }
        catch (JsonException) { return false; }
    }

    private async Task<IReadOnlyDictionary<Guid, CustomFieldEntry>> LoadCustomFieldValuesForAgentAsync(
        Guid agentId,
        Guid siteId,
        CancellationToken cancellationToken)
    {
        var site = await _siteRepository.GetByIdAsync(siteId);
        var clientId = site?.ClientId;

        var agentKey = agentId.ToString("D");
        var siteKey = siteId.ToString("D");
        var clientKey = clientId?.ToString("D");

        var applicableKeys = clientKey is null
            ? new[] { agentKey, siteKey }
            : new[] { agentKey, siteKey, clientKey };

        var rawValues = await _db.CustomFieldValues
            .AsNoTracking()
            .Where(v => applicableKeys.Contains(v.EntityKey))
            .ToListAsync(cancellationToken);

        if (rawValues.Count == 0)
            return new Dictionary<Guid, CustomFieldEntry>();

        var definitionIds = rawValues.Select(v => v.DefinitionId).Distinct().ToList();
        var definitions = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(d => definitionIds.Contains(d.Id) && d.IsActive
                && (d.ScopeType == CustomFieldScopeType.Agent
                    || d.ScopeType == CustomFieldScopeType.Site
                    || d.ScopeType == CustomFieldScopeType.Client))
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var result = new Dictionary<Guid, CustomFieldEntry>();
        foreach (var value in rawValues)
        {
            if (!definitions.TryGetValue(value.DefinitionId, out var def))
                continue;

            // scope-aware key match: Agent=agentKey, Site=siteKey, Client=clientKey
            var expectedKey = def.ScopeType switch
            {
                CustomFieldScopeType.Agent => agentKey,
                CustomFieldScopeType.Site => siteKey,
                CustomFieldScopeType.Client => clientKey,
                _ => null
            };

            if (expectedKey is null || !string.Equals(value.EntityKey, expectedKey, StringComparison.OrdinalIgnoreCase))
                continue;

            result[value.DefinitionId] = new CustomFieldEntry(value.ValueJson, def.DataType);
        }

        return result;
    }

    private static bool HasCustomFieldConditions(AgentLabelRuleExpressionNodeDto node)
    {
        if (node.NodeType == AgentLabelNodeType.Condition)
        {
            return node.Field is AgentLabelField.AgentCustomField
                or AgentLabelField.ClientCustomField
                or AgentLabelField.SiteCustomField;
        }

        return node.Children.Any(HasCustomFieldConditions);
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
