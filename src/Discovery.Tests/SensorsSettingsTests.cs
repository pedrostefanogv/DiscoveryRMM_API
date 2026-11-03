using Discovery.Core.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Leitura tolerante da configuração global de sensores (blob JSON em
/// server_configurations.sensors_settings_json). Vazio/inválido NUNCA pode
/// derrubar o sync — cai nos defaults.
/// </summary>
public class SensorsSettingsTests
{
    [Test]
    public void EmptyOrInvalidJson_FallsBackToDefaults()
    {
        foreach (var json in new[] { null, "", "   ", "{}", "null", "{", "[]" })
        {
            var settings = SensorsSettings.Parse(json);
            Assert.That(settings.Enabled, Is.True, $"json={json}");
            Assert.That(settings.DefaultIntervalSeconds, Is.EqualTo(300), $"json={json}");
            Assert.That(settings.AutoTicketEnabled, Is.True, $"json={json}");
            Assert.That(settings.NotifyEnabled, Is.True, $"json={json}");
        }
    }

    [Test]
    public void ParsesCamelCaseCaseInsensitively()
    {
        var settings = SensorsSettings.Parse("""
            { "enabled": false, "defaultIntervalSeconds": 600, "autoTicketEnabled": false, "notifyEnabled": true }
            """);

        Assert.That(settings.Enabled, Is.False);
        Assert.That(settings.DefaultIntervalSeconds, Is.EqualTo(600));
        Assert.That(settings.AutoTicketEnabled, Is.False);
        Assert.That(settings.NotifyEnabled, Is.True);
    }

    [Test]
    public void TryParseStrict_RejectsInvalidJson()
    {
        Assert.That(SensorsSettings.TryParseStrict("{ \"enabled\": ", out _, out var error), Is.False,
            "PATCH de configuração não pode aceitar JSON inválido");
        Assert.That(error, Does.Contain("SensorsSettingsJson"));
    }

    [Test]
    public void TryParseStrict_AcceptsValidJsonAndEmpty()
    {
        Assert.That(SensorsSettings.TryParseStrict(null, out var empty, out _), Is.True);
        Assert.That(empty.Enabled, Is.True);
        Assert.That(SensorsSettings.TryParseStrict("{ \"defaultIntervalSeconds\": 120 }", out var parsed, out _), Is.True);
        Assert.That(parsed.EffectiveDefaultIntervalSeconds, Is.EqualTo(120));
    }

    [TestCase(0, 30)]
    [TestCase(29, 30)]
    [TestCase(300, 300)]
    [TestCase(100000, 86400)]
    public void EffectiveInterval_IsClamped(int configured, int expected)
    {
        var settings = new SensorsSettings { DefaultIntervalSeconds = configured };
        Assert.That(settings.EffectiveDefaultIntervalSeconds, Is.EqualTo(expected));
    }
}
