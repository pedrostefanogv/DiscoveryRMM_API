using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

public class AutoTicketRuleEngineService : IAutoTicketRuleEngineService
{
    private readonly IAutoTicketRuleRepository _ruleRepository;
    private readonly IMonitoringEventNormalizationService _normalizationService;

    public AutoTicketRuleEngineService(
        IAutoTicketRuleRepository ruleRepository,
        IMonitoringEventNormalizationService normalizationService)
    {
        _ruleRepository = ruleRepository;
        _normalizationService = normalizationService;
    }

    public async Task<AutoTicketRuleDecision> EvaluateAsync(
        AgentMonitoringEvent monitoringEvent,
        IReadOnlyCollection<string> labels,
        IReadOnlyCollection<AutoTicketRule>? candidateRules = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var rules = candidateRules ?? await _ruleRepository.GetAllAsync(isEnabled: true);
        var matchedRules = rules
            .Where(rule => rule.IsEnabled)
            .Where(rule => MatchesScope(rule, monitoringEvent))
            .Where(rule => MatchesAlertCode(rule, monitoringEvent.AlertCode))
            .Where(rule => MatchesSource(rule, monitoringEvent.Source))
            .Where(rule => MatchesSeverity(rule, monitoringEvent.Severity))
            .Where(rule => MatchesLabels(rule, labels))
            .Where(rule => MatchesPayloadPredicate(rule, monitoringEvent.PayloadJson))
            .OrderBy(GetActionRank)
            .ThenBy(GetScopeRank)
            .ThenByDescending(rule => rule.PriorityOrder)
            .ThenByDescending(rule => rule.SeverityMax.HasValue ? (int)rule.SeverityMax.Value : -1)
            .ThenByDescending(rule => rule.CreatedAt)
            .ToList();

        if (matchedRules.Count == 0)
        {
            return new AutoTicketRuleDecision
            {
                Decision = AutoTicketDecision.MatchedNoAction,
                Reason = "No AutoTicket rule matched the monitoring event."
            };
        }

        var winner = matchedRules[0];
        if (winner.Action == AutoTicketRuleAction.Suppress)
        {
            return new AutoTicketRuleDecision
            {
                Rule = winner,
                Decision = AutoTicketDecision.Suppressed,
                Reason = $"Rule '{winner.Name}' suppressed the monitoring event."
            };
        }

        if (winner.Action == AutoTicketRuleAction.AlertOnly)
        {
            return new AutoTicketRuleDecision
            {
                Rule = winner,
                Decision = AutoTicketDecision.MatchedNoAction,
                Reason = $"Rule '{winner.Name}' matched with action AlertOnly."
            };
        }

        return new AutoTicketRuleDecision
        {
            Rule = winner,
            Decision = AutoTicketDecision.MatchedNoAction,
            Reason = $"Rule '{winner.Name}' matched and requested ticket creation."
        };
    }

    private static int GetActionRank(AutoTicketRule rule)
        => rule.Action == AutoTicketRuleAction.Suppress ? 0 : 1;

    private static int GetScopeRank(AutoTicketRule rule)
        => rule.ScopeLevel switch
        {
            AutoTicketScopeLevel.Site => 0,
            AutoTicketScopeLevel.Client => 1,
            _ => 2
        };

    private static bool MatchesScope(AutoTicketRule rule, AgentMonitoringEvent monitoringEvent)
    {
        return rule.ScopeLevel switch
        {
            AutoTicketScopeLevel.Global => true,
            AutoTicketScopeLevel.Client => rule.ScopeId.HasValue && rule.ScopeId.Value == monitoringEvent.ClientId,
            AutoTicketScopeLevel.Site => rule.ScopeId.HasValue && monitoringEvent.SiteId.HasValue && rule.ScopeId.Value == monitoringEvent.SiteId.Value,
            _ => false
        };
    }

    private static bool MatchesAlertCode(AutoTicketRule rule, string alertCode)
    {
        if (string.IsNullOrWhiteSpace(rule.AlertCodeFilter))
            return true;

        return string.Equals(rule.AlertCodeFilter.Trim(), alertCode.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSource(AutoTicketRule rule, MonitoringEventSource source)
        => !rule.SourceFilter.HasValue || rule.SourceFilter.Value == source;

    private static bool MatchesSeverity(AutoTicketRule rule, MonitoringEventSeverity severity)
    {
        if (rule.SeverityMin.HasValue && severity < rule.SeverityMin.Value)
            return false;

        if (rule.SeverityMax.HasValue && severity > rule.SeverityMax.Value)
            return false;

        return true;
    }

    private bool MatchesLabels(AutoTicketRule rule, IReadOnlyCollection<string> labels)
    {
        var labelSet = labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var anyLabels = _normalizationService.DeserializeLabels(rule.MatchLabelsAnyJson);
        if (anyLabels.Count > 0 && !anyLabels.Any(labelSet.Contains))
            return false;

        var allLabels = _normalizationService.DeserializeLabels(rule.MatchLabelsAllJson);
        if (allLabels.Count > 0 && allLabels.Any(label => !labelSet.Contains(label)))
            return false;

        var excludedLabels = _normalizationService.DeserializeLabels(rule.ExcludeLabelsJson);
        if (excludedLabels.Count > 0 && excludedLabels.Any(labelSet.Contains))
            return false;

        return true;
    }

    private static bool MatchesPayloadPredicate(AutoTicketRule rule, string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(rule.PayloadPredicateJson))
            return true;

        if (string.IsNullOrWhiteSpace(payloadJson))
            return false;

        try
        {
            using var predicateDocument = JsonDocument.Parse(rule.PayloadPredicateJson);
            using var payloadDocument = JsonDocument.Parse(payloadJson);

            if (predicateDocument.RootElement.ValueKind != JsonValueKind.Object || payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var predicateProperty in predicateDocument.RootElement.EnumerateObject())
            {
                if (!payloadDocument.RootElement.TryGetProperty(predicateProperty.Name, out var payloadProperty))
                    return false;

                if (!JsonElementEquals(predicateProperty.Value, payloadProperty))
                    return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool JsonElementEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
            return string.Equals(left.GetString(), right.GetString(), StringComparison.OrdinalIgnoreCase);

        return left.GetRawText() == right.GetRawText();
    }
}