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

    private readonly MeduzaDbContext _db;
    private readonly IAgentRepository _agentRepository;
    private readonly IAgentHardwareRepository _hardwareRepository;
    private readonly IAgentSoftwareRepository _softwareRepository;
    private readonly IAgentLabelRuleRepository _ruleRepository;
    private readonly ILogger<AgentAutoLabelingService> _logger;

    public AgentAutoLabelingService(
        MeduzaDbContext db,
        IAgentRepository agentRepository,
        IAgentHardwareRepository hardwareRepository,
        IAgentSoftwareRepository softwareRepository,
        IAgentLabelRuleRepository ruleRepository,
        ILogger<AgentAutoLabelingService> logger)
    {
        _db = db;
        _agentRepository = agentRepository;
        _hardwareRepository = hardwareRepository;
        _softwareRepository = softwareRepository;
        _ruleRepository = ruleRepository;
        _logger = logger;
    }

    public async Task EvaluateAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent is null)
            return;

        var rules = await _ruleRepository.GetEnabledAsync();
        if (rules.Count == 0)
            return;

        var hardware = await _hardwareRepository.GetByAgentIdAsync(agentId);
        var software = (await _softwareRepository.GetCurrentByAgentIdAsync(agentId)).ToList();

        var now = DateTime.UtcNow;

        foreach (var rule in rules)
        {
            var expression = TryDeserializeExpression(rule.ExpressionJson);
            if (expression is null)
            {
                _logger.LogWarning("Agent label rule {RuleId} has invalid expression and was skipped.", rule.Id);
                continue;
            }

            var matched = EvaluateNode(expression, agent, hardware, software);
            await UpsertOrRemoveMatchAsync(rule, agentId, matched, now, cancellationToken);
        }

        await SyncEffectiveLabelsAsync(agentId, now, cancellationToken);

        _logger.LogInformation(
            "Agent auto-labeling evaluated for {AgentId}. Reason: {Reason}",
            agentId,
            reason);
    }

    public async Task ReprocessAllAgentsAsync(string reason, CancellationToken cancellationToken = default)
    {
        var agentIds = await _db.Agents
            .AsNoTracking()
            .OrderBy(agent => agent.Id)
            .Select(agent => agent.Id)
            .ToListAsync(cancellationToken);

        foreach (var agentId in agentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EvaluateAgentAsync(agentId, reason, cancellationToken);
        }
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

    private async Task UpsertOrRemoveMatchAsync(
        AgentLabelRule rule,
        Guid agentId,
        bool matched,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var existing = await _db.AgentLabelRuleMatches
            .SingleOrDefaultAsync(
                item => item.RuleId == rule.Id && item.AgentId == agentId,
                cancellationToken);

        if (matched)
        {
            if (existing is null)
            {
                _db.AgentLabelRuleMatches.Add(new AgentLabelRuleMatch
                {
                    Id = IdGenerator.NewId(),
                    RuleId = rule.Id,
                    AgentId = agentId,
                    Label = rule.Label,
                    MatchedAt = now,
                    LastEvaluatedAt = now
                });
            }
            else
            {
                existing.Label = rule.Label;
                existing.LastEvaluatedAt = now;
            }

            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (existing is null)
            return;

        if (rule.ApplyMode == AgentLabelApplyMode.ApplyAndRemove)
        {
            _db.AgentLabelRuleMatches.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        existing.LastEvaluatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task SyncEffectiveLabelsAsync(Guid agentId, DateTime now, CancellationToken cancellationToken)
    {
        var automaticLabelsFromMatches = await _db.AgentLabelRuleMatches
            .AsNoTracking()
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
        }

        var shouldKeep = automaticLabelsFromMatches
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var label in existingAutomaticLabels)
        {
            if (shouldKeep.Contains(label.Label))
            {
                label.UpdatedAt = now;
                continue;
            }

            _db.AgentLabels.Remove(label);
        }

        await _db.SaveChangesAsync(cancellationToken);
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
