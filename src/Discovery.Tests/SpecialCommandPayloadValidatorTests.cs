using System.Text.Json;
using Discovery.Api.Services;
using Discovery.Core.Enums;

namespace Discovery.Tests;

[TestFixture]
public class SpecialCommandPayloadValidatorTests
{
    [Test]
    public void UpdateInstall_ShouldRequireHttpsUrlAndVersion()
    {
        var validator = new SpecialCommandPayloadValidator();

        var payload = """
            {
              "action": "install",
              "version": "1.2.3",
              "url": "https://updates.example.com/discovery-agent.exe"
            }
            """;

        var ok = validator.TryNormalize(CommandType.Update, payload, out var normalizedPayload, out var error);

        Assert.That(ok, Is.True, error);

        using var json = JsonDocument.Parse(normalizedPayload);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("action").GetString(), Is.EqualTo("install"));
            Assert.That(json.RootElement.GetProperty("version").GetString(), Is.EqualTo("1.2.3"));
            Assert.That(json.RootElement.GetProperty("url").GetString(), Is.EqualTo("https://updates.example.com/discovery-agent.exe"));
        });
    }

    [Test]
    public void Update_ShouldRejectLegacyForceUpdateAction()
    {
        var validator = new SpecialCommandPayloadValidator();

        var payload = """
            {
              "action": "force-update"
            }
            """;

        var ok = validator.TryNormalize(CommandType.Update, payload, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("check-update"));
    }

    [Test]
    public void Notification_ShouldApplyDefaults()
    {
        var validator = new SpecialCommandPayloadValidator();

        var payload = """
            {
              "notificationId": "notif-1",
              "idempotencyKey": "key-1",
              "title": "Attention",
              "message": "Patch available",
              "eventType": "agent.update.available"
            }
            """;

        var ok = validator.TryNormalize(CommandType.Notification, payload, out var normalizedPayload, out var error);

        Assert.That(ok, Is.True, error);

        using var json = JsonDocument.Parse(normalizedPayload);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("mode").GetString(), Is.EqualTo("notify_only"));
            Assert.That(json.RootElement.GetProperty("severity").GetString(), Is.EqualTo("medium"));
            Assert.That(json.RootElement.GetProperty("layout").GetString(), Is.EqualTo("toast"));
            Assert.That(json.RootElement.GetProperty("timeoutSeconds").GetInt32(), Is.EqualTo(8));
        });
    }

    [Test]
        public void RemoteDebugStart_ShouldAcceptNatsOnlyStreamFields()
    {
        var validator = new SpecialCommandPayloadValidator();

        var payload = """
            {
              "action": "start",
              "sessionId": "26d6bd85-39f5-4ac6-bb11-c3cba9cfc6f6",
              "expiresAtUtc": "2026-05-05T10:00:00Z",
              "stream": {
                "natsSubject": "tenant.c.site.s.agent.a.remote-debug.log"
              }
            }
            """;

                var ok = validator.TryNormalize(CommandType.RemoteDebug, payload, out var normalizedPayload, out var error);

                Assert.That(ok, Is.True, error);

                using var json = JsonDocument.Parse(normalizedPayload);
                Assert.That(json.RootElement.GetProperty("stream").GetProperty("natsSubject").GetString(), Is.EqualTo("tenant.c.site.s.agent.a.remote-debug.log"));
    }
}