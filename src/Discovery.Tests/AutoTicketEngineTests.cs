using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

public class AutoTicketEngineTests
{
    [Test]
    public void BuildDedupKey_ShouldIgnoreVolatileFieldsAndPropertyOrder()
    {
        var normalizationService = new MonitoringEventNormalizationService();
        var fingerprintService = new DedupFingerprintService(normalizationService);
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var rule = new AutoTicketRule { DedupWindowMinutes = 60 };

        var firstEvent = new AgentMonitoringEvent
        {
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            AlertCode = "disk.full",
            OccurredAt = new DateTime(2026, 4, 17, 10, 5, 0, DateTimeKind.Utc),
            PayloadJson = "{\"timestamp\":\"2026-04-17T10:05:00Z\",\"payload\":{\"used\":95,\"disk\":\"C\"},\"requestId\":\"one\"}"
        };

        var secondEvent = new AgentMonitoringEvent
        {
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            AlertCode = "disk.full",
            OccurredAt = new DateTime(2026, 4, 17, 10, 45, 0, DateTimeKind.Utc),
            PayloadJson = "{\"requestId\":\"two\",\"payload\":{\"disk\":\"C\",\"used\":95},\"timestamp\":\"2026-04-17T10:45:00Z\"}"
        };

        var firstKey = fingerprintService.BuildDedupKey(firstEvent, rule);
        var secondKey = fingerprintService.BuildDedupKey(secondEvent, rule);

        Assert.That(secondKey, Is.EqualTo(firstKey));
    }

    [Test]
    public void BuildDedupKey_ShouldChangeWhenRelevantPayloadChanges()
    {
        var normalizationService = new MonitoringEventNormalizationService();
        var fingerprintService = new DedupFingerprintService(normalizationService);
        var rule = new AutoTicketRule { DedupWindowMinutes = 60 };
        var baseTime = new DateTime(2026, 4, 17, 10, 5, 0, DateTimeKind.Utc);

        var firstEvent = new AgentMonitoringEvent
        {
            ClientId = Guid.NewGuid(),
            SiteId = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            AlertCode = "cpu.high",
            OccurredAt = baseTime,
            PayloadJson = "{\"metric\":\"cpu\",\"value\":85}"
        };

        var secondEvent = new AgentMonitoringEvent
        {
            ClientId = firstEvent.ClientId,
            SiteId = firstEvent.SiteId,
            AgentId = firstEvent.AgentId,
            AlertCode = firstEvent.AlertCode,
            OccurredAt = baseTime,
            PayloadJson = "{\"metric\":\"cpu\",\"value\":95}"
        };

        var firstKey = fingerprintService.BuildDedupKey(firstEvent, rule);
        var secondKey = fingerprintService.BuildDedupKey(secondEvent, rule);

        Assert.That(secondKey, Is.Not.EqualTo(firstKey));
    }

    [Test]
    public async Task EvaluateAsync_ShouldPreferSuppressSiteRuleOverLessSpecificCreateRules()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var engine = new AutoTicketRuleEngineService(new FakeAutoTicketRuleRepository(), new MonitoringEventNormalizationService());
        var monitoringEvent = new AgentMonitoringEvent
        {
            ClientId = clientId,
            SiteId = siteId,
            AgentId = Guid.NewGuid(),
            AlertCode = "disk.full",
            Severity = MonitoringEventSeverity.Critical,
            Source = MonitoringEventSource.Automation,
            PayloadJson = "{\"disk\":\"C\",\"used\":95}"
        };

        var globalCreate = new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "global-create",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Global,
            AlertCodeFilter = "disk.full",
            Action = AutoTicketRuleAction.CreateTicket,
            PriorityOrder = 100,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        };

        var clientCreate = new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "client-create",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Client,
            ScopeId = clientId,
            AlertCodeFilter = "disk.full",
            Action = AutoTicketRuleAction.CreateTicket,
            PriorityOrder = 50,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var siteSuppress = new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "site-suppress",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Site,
            ScopeId = siteId,
            AlertCodeFilter = "disk.full",
            Action = AutoTicketRuleAction.Suppress,
            PriorityOrder = 1,
            CreatedAt = DateTime.UtcNow
        };

        var result = await engine.EvaluateAsync(monitoringEvent, ["servidor"], [globalCreate, clientCreate, siteSuppress]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Rule?.Id, Is.EqualTo(siteSuppress.Id));
            Assert.That(result.Decision, Is.EqualTo(AutoTicketDecision.Suppressed));
            Assert.That(result.IsSuppressed, Is.True);
        });
    }

    [Test]
    public async Task EvaluateAsync_ShouldPreferMostSpecificCreateRuleWhenMultipleRulesMatch()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var engine = new AutoTicketRuleEngineService(new FakeAutoTicketRuleRepository(), new MonitoringEventNormalizationService());
        var monitoringEvent = new AgentMonitoringEvent
        {
            ClientId = clientId,
            SiteId = siteId,
            AgentId = Guid.NewGuid(),
            AlertCode = "cpu.high",
            Severity = MonitoringEventSeverity.Warning,
            Source = MonitoringEventSource.Automation,
            PayloadJson = "{\"metric\":\"cpu\",\"value\":92}"
        };

        var clientRule = new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "client-rule",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Client,
            ScopeId = clientId,
            AlertCodeFilter = "cpu.high",
            Action = AutoTicketRuleAction.CreateTicket,
            PriorityOrder = 100,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var siteRule = new AutoTicketRule
        {
            Id = Guid.NewGuid(),
            Name = "site-rule",
            IsEnabled = true,
            ScopeLevel = AutoTicketScopeLevel.Site,
            ScopeId = siteId,
            AlertCodeFilter = "cpu.high",
            Action = AutoTicketRuleAction.CreateTicket,
            PriorityOrder = 10,
            CreatedAt = DateTime.UtcNow
        };

        var result = await engine.EvaluateAsync(monitoringEvent, ["servidor"], [clientRule, siteRule]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Rule?.Id, Is.EqualTo(siteRule.Id));
            Assert.That(result.ShouldCreateTicket, Is.True);
            Assert.That(result.Decision, Is.EqualTo(AutoTicketDecision.MatchedNoAction));
        });
    }

    private sealed class FakeAutoTicketRuleRepository : IAutoTicketRuleRepository
    {
        public Task<AutoTicketRule?> GetByIdAsync(Guid id) => Task.FromResult<AutoTicketRule?>(null);

        public Task<IReadOnlyList<AutoTicketRule>> GetAllAsync(
            AutoTicketScopeLevel? scopeLevel = null,
            Guid? scopeId = null,
            bool? isEnabled = null,
            string? alertCode = null)
            => Task.FromResult<IReadOnlyList<AutoTicketRule>>([]);

        public Task<AutoTicketRule> CreateAsync(AutoTicketRule rule) => Task.FromResult(rule);

        public Task<AutoTicketRule> UpdateAsync(AutoTicketRule rule) => Task.FromResult(rule);

        public Task<bool> DeleteAsync(Guid id) => Task.FromResult(true);
    }
}