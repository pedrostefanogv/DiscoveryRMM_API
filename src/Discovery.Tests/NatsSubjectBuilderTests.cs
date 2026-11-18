using Discovery.Core.Helpers;

namespace Discovery.Tests;

public class NatsSubjectBuilderTests
{
    [Test]
    public void AgentSubject_ShouldUseCanonicalTenantScopedFormat()
    {
        var clientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var siteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var agentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var subject = NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, "heartbeat");

        Assert.That(subject, Is.EqualTo($"tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat"));
        Assert.That(subject.StartsWith("agent."), Is.False);
    }

    [Test]
    public void DashboardSubject_WithClientAndSite_ShouldBeTenantScoped()
    {
        var clientId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var siteId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var subject = NatsSubjectBuilder.DashboardSubject(clientId, siteId);

        Assert.That(subject, Is.EqualTo($"tenant.{clientId}.site.{siteId}.dashboard.events"));
    }

    [Test]
    public void DashboardSubject_WithClientOnly_ShouldBeTenantScoped()
    {
        var clientId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var subject = NatsSubjectBuilder.DashboardSubject(clientId, null);

        Assert.That(subject, Is.EqualTo($"tenant.{clientId}.dashboard.events"));
    }

    [Test]
    public void DashboardSubject_WithoutClientScope_ShouldReturnUnscopedFallback()
    {
        var subject = NatsSubjectBuilder.DashboardSubject(null, null);

        Assert.That(subject, Is.EqualTo("tenant.unscoped.dashboard.events"));
    }

    [Test]
    public void SiteAgentsCommandSubject_ShouldUseCanonicalTenantScopedFormat()
    {
        var clientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var siteId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var subject = NatsSubjectBuilder.SiteAgentsCommandSubject(clientId, siteId);

        Assert.That(subject, Is.EqualTo($"tenant.{clientId}.site.{siteId}.agents.command"));
    }

    [Test]
    public void ClientAgentsCommandSubject_ShouldUseCanonicalTenantScopedFormat()
    {
        var clientId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var subject = NatsSubjectBuilder.ClientAgentsCommandSubject(clientId);

        Assert.That(subject, Is.EqualTo($"tenant.{clientId}.agents.command"));
    }

    [Test]
    public void GlobalAgentsCommandSubject_ShouldUseCanonicalGlobalFormat()
    {
        var subject = NatsSubjectBuilder.GlobalAgentsCommandSubject();

        Assert.That(subject, Is.EqualTo("tenant.global.agents.command"));
    }

    [Test]
    public void ServerPongSubject_ShouldUseCanonicalGlobalFormat()
    {
        var subject = NatsSubjectBuilder.ServerPongSubject();

        Assert.That(subject, Is.EqualTo("tenant.global.pong"));
    }

    [Test]
    public void RemoteDebugControlSubject_ShouldUseCanonicalAgentFormat()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var subject = NatsSubjectBuilder.RemoteDebugControlSubject(clientId, siteId, agentId);

        Assert.That(
            subject,
            Is.EqualTo($"tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.control"));
    }

    [Test]
    public void TenantStreamHelpers_ShouldFormatPerTenantNamesAndHyphenatedSubjects()
    {
        var clientId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.That(NatsSubjectBuilder.TenantFanoutStreamName(clientId),
            Is.EqualTo($"DISCOVERY_FANOUT_COMMANDS_{clientId:N}"));
        Assert.That(NatsSubjectBuilder.TenantSupportChatStreamName(clientId),
            Is.EqualTo($"DISCOVERY_SUPPORT_CHAT_{clientId:N}"));
        Assert.That(NatsSubjectBuilder.GlobalFanoutStreamName,
            Is.EqualTo("DISCOVERY_FANOUT_COMMANDS_GLOBAL"));
        Assert.That(NatsSubjectBuilder.TenantSiteAgentsCommandStreamSubject(clientId),
            Is.EqualTo($"tenant.{clientId}.site.*.agents.command"));
        Assert.That(NatsSubjectBuilder.TenantClientAgentsCommandStreamSubject(clientId),
            Is.EqualTo($"tenant.{clientId}.agents.command"));
        Assert.That(NatsSubjectBuilder.TenantSupportChatStreamSubject(clientId),
            Is.EqualTo($"tenant.{clientId}.site.*.agent.*.supportchat.*"));
    }

    [Test]
    public void TryParseAgentSubject_ShouldAcceptCanonicalAndRejectInvalidSubjects()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        Assert.That(
            NatsSubjectBuilder.TryParseAgentSubject(
                $"tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat",
                out var parsedClient,
                out var parsedSite,
                out var parsedAgent),
            Is.True);
        Assert.That(parsedClient, Is.EqualTo(clientId));
        Assert.That(parsedSite, Is.EqualTo(siteId));
        Assert.That(parsedAgent, Is.EqualTo(agentId));

        // Multi-segment suffix like supportchat.agent is also valid
        Assert.That(
            NatsSubjectBuilder.TryParseAgentSubject(
                $"tenant.{clientId}.site.{siteId}.agent.{agentId}.supportchat.agent",
                out _, out _, out _),
            Is.True);

        // Reject legacy, malformed, wildcard, trailing-dot, and empty-GUID subjects
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"agent.{agentId}.heartbeat", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"tenant.not-a-guid.site.{siteId}.agent.{agentId}.heartbeat", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"tenant.{Guid.Empty}.site.{siteId}.agent.{agentId}.heartbeat", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"tenant.{clientId}.site.{Guid.Empty}.agent.{agentId}.heartbeat", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"tenant.{clientId}.site.{siteId}.agent.{Guid.Empty}.heartbeat", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"tenant.{clientId}.site.{siteId}.agent.{agentId}.", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat.", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"tenant.{clientId}.site.{siteId}.agent.{agentId}..heartbeat", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"tenant.{clientId}.site.{siteId}.agent.{agentId}.*", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"tenant.{clientId}.site.{siteId}.agent.{agentId}.>", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject($"tenant.{clientId}.site.{siteId}.agent.{agentId}.supportchat.*", out _, out _, out _), Is.False);
        Assert.That(NatsSubjectBuilder.TryParseAgentSubject(null, out _, out _, out _), Is.False);
    }
}
