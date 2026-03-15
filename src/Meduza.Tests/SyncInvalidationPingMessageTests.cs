using System.Text.Json;
using Meduza.Core.DTOs;
using Meduza.Core.Enums;

namespace Meduza.Tests;

public class SyncInvalidationPingMessageTests
{
    [Test]
    public void FromDto_WhenSerialized_UsesStringEnumsAndCamelCaseFields()
    {
        var changedAtUtc = new DateTime(2026, 3, 15, 12, 30, 0, DateTimeKind.Utc);
        var ping = new SyncInvalidationPingDto
        {
            EventId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AgentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Resource = SyncResourceType.AppStore,
            ScopeType = AppApprovalScopeType.Site,
            ScopeId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            InstallationType = AppInstallationType.Winget,
            Revision = "appstore:winget:1773559080126",
            Reason = "catalog-updated",
            ChangedAtUtc = changedAtUtc,
            CorrelationId = "corr-123"
        };

        var message = SyncInvalidationPingMessage.FromDto(ping);
        var json = JsonSerializer.Serialize(message);

        Assert.Multiple(() =>
        {
            Assert.That(message.Resource, Is.EqualTo("AppStore"));
            Assert.That(message.ScopeType, Is.EqualTo("Site"));
            Assert.That(message.InstallationType, Is.EqualTo("Winget"));
        });

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("resource").GetString(), Is.EqualTo("AppStore"));
            Assert.That(root.GetProperty("scopeType").GetString(), Is.EqualTo("Site"));
            Assert.That(root.GetProperty("installationType").GetString(), Is.EqualTo("Winget"));
            Assert.That(root.TryGetProperty("changedAtUtc", out _), Is.True);
            Assert.That(root.TryGetProperty("eventId", out _), Is.True);
        });
    }
}