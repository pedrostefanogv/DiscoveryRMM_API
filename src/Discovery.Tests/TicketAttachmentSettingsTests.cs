using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;

namespace Discovery.Tests;

/// <summary>
/// O PUT /configurations/server/ticket-attachments valida o payload com
/// TicketAttachmentSettings.Validate. Estes testes garantem os limites dos dois
/// níveis (conversa x repositório do chamado) e o fallback às colunas legadas.
/// </summary>
public class TicketAttachmentSettingsTests
{
    [Test]
    public void Validate_AcceptsDefaults()
    {
        var settings = new TicketAttachmentSettings();

        Assert.That(settings.MaxFileSizeBytes, Is.EqualTo(10L * 1024 * 1024));
        Assert.That(settings.MaxLargeFileSizeBytes, Is.EqualTo(2L * 1024 * 1024 * 1024));
        Assert.That(settings.Validate(), Is.Empty);
    }

    [Test]
    public void Validate_AcceptsRepositorySizesUpTo10Gb()
    {
        // O teto acompanha o nginx (client_max_body_size 10240m): 2 GB e 10 GiB
        // sao configuracoes validas para o repositorio do chamado.
        var twoGb = new TicketAttachmentSettings { MaxLargeFileSizeBytes = 2L * 1024 * 1024 * 1024 };
        Assert.That(twoGb.Validate(), Is.Empty);

        var tenGb = new TicketAttachmentSettings { MaxLargeFileSizeBytes = 10L * 1024 * 1024 * 1024 };
        Assert.That(tenGb.Validate(), Is.Empty);
    }

    [Test]
    public void Validate_RejectsLargeLimitAbove10Gb()
    {
        var settings = new TicketAttachmentSettings { MaxLargeFileSizeBytes = 11L * 1024 * 1024 * 1024 };

        Assert.That(settings.Validate(), Has.Some.Contains("MaxLargeFileSizeBytes"));
    }

    [Test]
    public void Validate_RejectsLargeLimitBelowConversationLimit()
    {
        var settings = new TicketAttachmentSettings
        {
            MaxFileSizeBytes = 20L * 1024 * 1024,
            MaxLargeFileSizeBytes = 15L * 1024 * 1024
        };

        Assert.That(settings.Validate(), Has.Some.Contains("MaxLargeFileSizeBytes"));
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
        var json = "{\"enabled\":false,\"maxFileSizeBytes\":5242880,\"maxLargeFileSizeBytes\":2147483648,\"allowedContentTypes\":[\" IMAGE/PNG \",\"image/png\"],\"presignedUploadUrlTtlMinutes\":30}";

        var settings = TicketAttachmentSettings.FromJson(json);

        Assert.That(settings.Enabled, Is.False);
        Assert.That(settings.MaxFileSizeBytes, Is.EqualTo(5242880));
        Assert.That(settings.MaxLargeFileSizeBytes, Is.EqualTo(2147483648L));
        Assert.That(settings.PresignedUploadUrlTtlMinutes, Is.EqualTo(30));
        Assert.That(settings.AllowedContentTypes, Is.EqualTo(new[] { "image/png" }));
        Assert.That(TicketAttachmentSettings.FromJson(settings.ToJson()).AllowedContentTypes, Is.EqualTo(new[] { "image/png" }));
    }

    [Test]
    public void FromJson_DefaultsLargeLimitWhenPropertyMissing()
    {
        var settings = TicketAttachmentSettings.FromJson("{\"maxFileSizeBytes\":5242880}");

        Assert.That(settings.MaxLargeFileSizeBytes, Is.EqualTo(2L * 1024 * 1024 * 1024));
        Assert.That(settings.Validate(), Is.Empty);
    }

    [Test]
    public void FromJson_FallsBackToDefaultsOnInvalidJson()
    {
        var settings = TicketAttachmentSettings.FromJson("{not-json");

        Assert.That(settings.Enabled, Is.True);
        Assert.That(settings.AllowedContentTypes, Is.Not.Empty);
        Assert.That(settings.MaxLargeFileSizeBytes, Is.EqualTo(2L * 1024 * 1024 * 1024));
    }

    [Test]
    public void FromServerConfiguration_UsesLegacyColumnsWhenJsonIsEmpty()
    {
        var server = new ServerConfiguration
        {
            TicketAttachmentSettingsJson = "{}",
            AttachmentCommentMaxFileSizeBytes = 7L * 1024 * 1024,
            AttachmentCommentAllowedContentTypes = "image/png,application/pdf",
            AttachmentTicketMaxFileSizeBytes = 3L * 1024 * 1024 * 1024,
            AttachmentTicketAllowedContentTypes = "image/png,application/pdf,application/zip"
        };

        var settings = TicketAttachmentSettings.FromServerConfiguration(server);

        Assert.That(settings.MaxFileSizeBytes, Is.EqualTo(7L * 1024 * 1024));
        Assert.That(settings.MaxLargeFileSizeBytes, Is.EqualTo(3L * 1024 * 1024 * 1024));
        // Allowlist legada de COMENTÁRIO é a preferida (mais restrita).
        Assert.That(settings.AllowedContentTypes,
            Is.EqualTo(new[] { "image/png", "application/pdf" }));
    }

    [Test]
    public void FromServerConfiguration_FallsBackToRepositoryAllowlistWhenCommentIsEmpty()
    {
        var server = new ServerConfiguration
        {
            TicketAttachmentSettingsJson = "{}",
            AttachmentCommentAllowedContentTypes = "",
            AttachmentTicketAllowedContentTypes = "image/png,application/zip"
        };

        var settings = TicketAttachmentSettings.FromServerConfiguration(server);

        Assert.That(settings.AllowedContentTypes,
            Is.EqualTo(new[] { "image/png", "application/zip" }));
    }

    [Test]
    public void Validate_DoesNotThrowWhenContentTypesIsNull()
    {
        var settings = new TicketAttachmentSettings { AllowedContentTypes = null! };

        Assert.That(settings.Validate(), Has.Some.Contains("AllowedContentTypes"));
    }

    [Test]
    public void GetAllowedImageContentTypes_FiltersNonImages()
    {
        var settings = new TicketAttachmentSettings
        {
            AllowedContentTypes = ["image/png", "application/pdf", "image/jpeg"]
        };

        Assert.That(settings.GetAllowedImageContentTypes(),
            Is.EqualTo(new[] { "image/png", "image/jpeg" }));
    }
}
