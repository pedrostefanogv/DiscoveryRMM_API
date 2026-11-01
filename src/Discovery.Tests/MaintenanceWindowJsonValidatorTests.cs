using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Cqrs.Configurations;

namespace Discovery.Tests;

/// <summary>
/// Contrato do blob maintenanceWindowJson (server/client/site): janela local
/// (timeZone IANA + startTime HH:mm), janela legada (startUtc UTC) e recusa
/// de valores que fariam o agent cair em UTC silenciosamente.
/// </summary>
public class MaintenanceWindowJsonValidatorTests
{
    [Test]
    public void Validate_AcceptsLocalWindowWithIanaTimeZoneAndStartTime()
    {
        var json = """{"enabled": true, "daysOfWeek": [1,2,3,4,5], "startTime": "23:00", "timeZone": "America/Sao_Paulo", "durationMinutes": 60, "rebootGraceSeconds": 60}""";

        Assert.That(MaintenanceWindowJsonValidator.Validate(json), Is.Empty);
    }

    [Test]
    public void Validate_RejectsInvalidIanaTimeZone()
    {
        var json = """{"enabled": true, "startTime": "23:00", "timeZone": "America/Sao_Pauloooo"}""";

        var errors = MaintenanceWindowJsonValidator.Validate(json);

        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0], Does.Contain("maintenanceWindowJson.timeZone inválido: America/Sao_Pauloooo"));
    }

    [Test]
    public void Validate_RejectsInvalidStartTimeFormat()
    {
        var json = """{"enabled": true, "startTime": "24:30", "timeZone": "America/Sao_Paulo"}""";

        var errors = MaintenanceWindowJsonValidator.Validate(json);

        Assert.That(errors, Does.Contain(
            "maintenanceWindowJson.startTime inválido: 24:30 (esperado HH:mm, 00:00-23:59)"));
    }

    [Test]
    public void Validate_RejectsNonNumericStartTime()
    {
        var json = """{"enabled": true, "startTime": "9:0a"}""";

        var errors = MaintenanceWindowJsonValidator.Validate(json);

        Assert.That(errors[0], Does.Contain("startTime inválido"));
    }

    [Test]
    public void Validate_AcceptsLegacyJsonWithoutNewFields()
    {
        var json = """{"enabled": true, "daysOfWeek": [1,2,3,4,5], "startUtc": "02:00", "durationMinutes": 60}""";

        Assert.That(MaintenanceWindowJsonValidator.Validate(json), Is.Empty);
    }

    [Test]
    public void Validate_AcceptsNullEmptyAndWhitespace()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MaintenanceWindowJsonValidator.Validate(null), Is.Empty);
            Assert.That(MaintenanceWindowJsonValidator.Validate(""), Is.Empty);
            Assert.That(MaintenanceWindowJsonValidator.Validate("   "), Is.Empty);
        });
    }

    [Test]
    public void Validate_RejectsMalformedJson()
    {
        var errors = MaintenanceWindowJsonValidator.Validate("{enabled: ");

        Assert.That(errors[0], Does.Contain("JSON inválido"));
    }

    [Test]
    public void Validate_RejectsNonObjectRoot()
    {
        var errors = MaintenanceWindowJsonValidator.Validate("[1,2,3]");

        Assert.That(errors[0], Does.Contain("deve ser um objeto"));
    }
    [Test]
    public void ValidatePatchValue_AcceptsJsonElementPatchPayload()
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            enabled = true,
            startTime = "02:00",
            timeZone = "Europe/Lisbon"
        });

        Assert.That(MaintenanceWindowJsonValidator.ValidatePatchValue(element), Is.Empty);
    }

    [Test]
    public void ValidatePatchValue_RejectsJsonElementWithBadTimeZone()
    {
        var element = JsonSerializer.SerializeToElement(new { timeZone = "Mars/Olympus_Mons" });

        var errors = MaintenanceWindowJsonValidator.ValidatePatchValue(element);

        Assert.That(errors[0], Does.Contain("timeZone inválido: Mars/Olympus_Mons"));
    }

    [Test]
    public void ValidatePatchValue_IgnoresNullValueAsInherit()
    {
        Assert.That(MaintenanceWindowJsonValidator.ValidatePatchValue(null), Is.Empty);
    }

    [Test]
    public void ValidatePatch_FindsKeyCaseInsensitively()
    {
        var updates = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["maintenanceWindowJson"] = """{"timeZone": "America/Sao_Paulo", "startTime": "23:00"}"""
        };

        Assert.That(MaintenanceWindowJsonValidator.ValidatePatch(updates), Is.Empty);
    }

    [Test]
    public void ValidatePatch_ReturnsValidationErrorsWithFieldName()
    {
        var updates = new Dictionary<string, object>
        {
            ["maintenanceWindowJson"] = """{"timeZone": "America/Sao_Pauloooo"}"""
        };

        var errors = MaintenanceWindowJsonValidator.ValidatePatch(updates);

        Assert.That(errors.Count, Is.EqualTo(1));
        Assert.That(errors[0].Field, Is.EqualTo("MaintenanceWindowJson"));
    }

    [Test]
    public void ValidatePatch_ReturnsEmptyWhenFieldNotPatched()
    {
        var updates = new Dictionary<string, object> { ["agentHeartbeatIntervalSeconds"] = 60 };

        Assert.That(MaintenanceWindowJsonValidator.ValidatePatch(updates), Is.Empty);
    }

    [Test]
    public void ValidateConfiguration_AcceptsValidSiteBlob()
    {
        var site = new SiteConfiguration
        {
            MaintenanceWindowJson = """{"enabled": true, "startTime": "23:00", "timeZone": "America/Sao_Paulo"}"""
        };

        Assert.That(MaintenanceWindowJsonValidator.ValidateConfiguration(site), Is.Empty);
    }

    [Test]
    public void ValidateConfiguration_ReturnsErrorsForInvalidClientBlob()
    {
        var client = new ClientConfiguration
        {
            MaintenanceWindowJson = """{"startTime": "23x0"}"""
        };

        var errors = MaintenanceWindowJsonValidator.ValidateConfiguration(client);

        Assert.That(errors[0].Message, Does.Contain("startTime inválido: 23x0"));
    }
}