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
    public void DashboardSubject_WithoutClientScope_ShouldThrow()
    {
        Assert.Throws<InvalidOperationException>(() => NatsSubjectBuilder.DashboardSubject(null, null));
    }
}
