using Discovery.Api.Controllers;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// O chat de IA/LLM usa a MESMA configuração de anexos dos tickets: allowlist
/// de imagens e limite por arquivo (MaxFileSizeBytes). A validação acontece no
/// AgentAuthController antes de abrir o stream.
/// </summary>
public class AiChatAttachmentPolicyTests
{
    private static string DataUrl(string mime, int bytes)
        => $"data:{mime};base64,{Convert.ToBase64String(new byte[bytes])}";

    [Test]
    public void ValidateAiChatImages_AcceptsImageWithinLimit()
    {
        var settings = new TicketAttachmentSettings
        {
            AllowedContentTypes = ["image/png", "application/pdf"],
            MaxFileSizeBytes = 1024 * 1024
        };

        var (error, status) = AgentAuthController.ValidateAiChatImages(
            [DataUrl("image/png", 1024)], settings);

        Assert.That(error, Is.Null);
        Assert.That(status, Is.EqualTo(200));
    }

    [Test]
    public void ValidateAiChatImages_RejectsContentTypeOutsideAllowlist()
    {
        var settings = new TicketAttachmentSettings
        {
            AllowedContentTypes = ["image/png"]
        };

        var (error, status) = AgentAuthController.ValidateAiChatImages(
            [DataUrl("image/jpeg", 1024)], settings);

        Assert.That(error, Does.Contain("não permitido"));
        Assert.That(status, Is.EqualTo(400));
    }

    [Test]
    public void ValidateAiChatImages_RejectsImageAboveConfiguredLimit()
    {
        var settings = new TicketAttachmentSettings
        {
            AllowedContentTypes = ["image/png"],
            MaxFileSizeBytes = 1024
        };

        var (error, status) = AgentAuthController.ValidateAiChatImages(
            [DataUrl("image/png", 2048)], settings);

        Assert.That(error, Does.Contain("excedem o limite"));
        Assert.That(status, Is.EqualTo(413));
    }

    [Test]
    public void EstimateBase64DecodedBytes_AccountsForPadding()
    {
        // 1025 bytes geram base64 com padding ("="); a estimativa precisa bater
        // exatamente (antes subestimava em 1-2 bytes e deixava passar acima do limite).
        var payload = Convert.ToBase64String(new byte[1025]);

        Assert.That(AgentAuthController.EstimateBase64DecodedBytes(payload), Is.EqualTo(1025));
    }

    [Test]
    public void BuildImagePartsFromDataUrls_UsesConfiguredPerImageCap()
    {
        var settings = new TicketAttachmentSettings
        {
            MaxFileSizeBytes = 2 * 1024 * 1024
        };
        var cap = AiChatHelpers.Base64CharsForBytes(settings.MaxFileSizeBytes);
        var within = DataUrl("image/png", 1024 * 1024);
        var aboveCap = DataUrl("image/png", (int)settings.MaxFileSizeBytes + 1024 * 1024);

        var parts = AiChatHelpers.BuildImagePartsFromDataUrlsWithBudget(
            [within, aboveCap], logger: null,
            maxTotalBase64Chars: AiChatHelpers.MaxTotalImageBase64Chars,
            out var omitted, maxImageBase64Chars: cap);

        Assert.That(parts, Has.Count.EqualTo(1));
        Assert.That(omitted, Is.EqualTo(1));
    }
}
