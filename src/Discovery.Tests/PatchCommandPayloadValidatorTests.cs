using Discovery.Api.Services;
using Discovery.Core.Enums;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class PatchCommandPayloadValidatorTests
{
    private static (bool Ok, string Payload, string Error) Normalize(string json)
    {
        var validator = new SpecialCommandPayloadValidator();
        var ok = validator.TryNormalize(CommandType.WindowsUpdate, json, out var payload, out var error);
        return (ok, payload, error);
    }

    [Test]
    public void AcceptsScan()
    {
        var (ok, payload, _) = Normalize("""{ "action": "scan" }""");
        Assert.That(ok, Is.True);
        Assert.That(payload, Does.Contain("\"action\":\"scan\""));
    }

    [Test]
    public void AcceptsInstallAndNormalizesFields()
    {
        var (ok, payload, error) = Normalize("""
        {
          "action": "install",
          "updates": [
            {
              "updateKey": "guid:3c3f1f1a-1111-2222-3333-444455556666:12",
              "updateId": "3C3F1F1A-1111-2222-3333-444455556666",
              "revision": 12,
              "kbIds": ["5034441"]
            }
          ],
          "postAction": "RESTART",
          "ttlSeconds": 999999,
          "force": false
        }
        """);

        Assert.That(ok, Is.True, error);
        Assert.That(payload, Does.Contain("3c3f1f1a-1111-2222-3333-444455556666"));
        Assert.That(payload, Does.Contain("KB5034441"));
        Assert.That(payload, Does.Contain("\"postAction\":\"restart\""));
        Assert.That(payload, Does.Contain("\"ttlSeconds\":7200"));
    }

    [TestCase("""{ "action": "install", "updates": [] }""")]
    [TestCase("""{ "action": "install", "updates": [{ "updateKey": "lixo" }] }""")]
    [TestCase("""{ "action": "install", "updates": [{ "updateKey": "guid:abc:1" }] }""")]
    [TestCase("""{ "action": "install", "updates": [{ "updateKey": "kb:KB1234" }], "postAction": "hack" }""")]
    [TestCase("""{ "action": "install", "updates": [{ "updateKey": "kb:KB1234" }], "ttlSeconds": "10" }""")]
    [TestCase("""{ "action": "reboot" }""")]
    [TestCase("""{ "action": "hide", "updateKeys": [] }""")]
    [TestCase("""{ "action": "hide", "updateKeys": ["nope"] }""")]
    public void RejectsInvalidPayloads(string json)
    {
        var (ok, _, error) = Normalize(json);
        Assert.That(ok, Is.False);
        Assert.That(error, Is.Not.Empty);
    }

    [Test]
    public void AcceptsPolicyAndRollback()
    {
        var (okPolicy, _, errorPolicy) = Normalize("""
        { "action": "policy", "settings": { "noAutoUpdate": 0, "auOptions": 2, "deferQualityUpdates": 1 } }
        """);
        Assert.That(okPolicy, Is.True, errorPolicy);

        var (okRollback, _, errorRollback) = Normalize("""
        { "action": "policy-rollback", "backup": { "capturedAt": "2024-01-01T00:00:00Z", "values": {} } }
        """);
        Assert.That(okRollback, Is.True, errorRollback);
    }
}
