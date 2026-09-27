using Discovery.Core.ValueObjects;

namespace Discovery.Tests;

/// <summary>
/// O PUT /configurations/server/ticket-attachments valida o payload com
/// TicketAttachmentSettings.Validate. Estes testes garantem os limites.
/// </summary>
public class TicketAttachmentSettingsTests
{
    [Test]
    public void Validate_AcceptsDefaults()
    {
        var settings = new TicketAttachmentSettings();

        Assert.That(settings.Validate(), Is.Empty);
    }

    [Test]
    public void Validate_RejectsOversizedFile()
    {
        var settings = new TicketAttachmentSettings { MaxFileSizeBytes = 2L * 1024 * 1024 * 1024 };

        Assert.That(settings.Validate(), Has.Some.Contains("MaxFileSizeBytes"));
    }

    [Test]
    public void Validate_RejectsInvalidTtl()
    {
        var settings = new TicketAttachmentSettings { PresignedUploadUrlTtlMinutes = 0 };

        Assert.That(settings.Validate(), Has.Some.Contains("PresignedUploadUrlTtlMinutes"));
    }

    [Test]
    public void Validate_RejectsEmptyContentTypes()
    {
        var settings = new TicketAttachmentSettings { AllowedContentTypes = [] };

        Assert.That(settings.Validate(), Has.Some.Contains("AllowedContentTypes"));
    }

    [Test]
    public void FromJson_RoundTripsAndNormalizesContentTypes()
    {
        var json = "{\"enabled\":false,\"maxFileSizeBytes\":5242880,\"allowedContentTypes\":[\" IMAGE/PNG \",\"image/png\"],\"presignedUploadUrlTtlMinutes\":30}";

        var settings = TicketAttachmentSettings.FromJson(json);

        Assert.That(settings.Enabled, Is.False);
        Assert.That(settings.MaxFileSizeBytes, Is.EqualTo(5242880));
        Assert.That(settings.PresignedUploadUrlTtlMinutes, Is.EqualTo(30));
        Assert.That(settings.AllowedContentTypes, Is.EqualTo(new[] { "image/png" }));
        Assert.That(TicketAttachmentSettings.FromJson(settings.ToJson()).AllowedContentTypes, Is.EqualTo(new[] { "image/png" }));
    }

    [Test]
    public void FromJson_FallsBackToDefaultsOnInvalidJson()
    {
        var settings = TicketAttachmentSettings.FromJson("{not-json");

        Assert.That(settings.Enabled, Is.True);
        Assert.That(settings.AllowedContentTypes, Is.Not.Empty);
    }
}
