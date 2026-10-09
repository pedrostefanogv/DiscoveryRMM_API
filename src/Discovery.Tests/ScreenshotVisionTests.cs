using System.Text.Json;
using Discovery.Api.Controllers;
using Discovery.Core.DTOs;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes da visão do chat IA (captura de tela assistida): extração das partes
/// multimodais do tool result do agent (contrato {"image_base64","mime"}) e dos
/// anexos enviados pelo usuário (campo "images").
/// </summary>
public class ScreenshotVisionTests
{
    /// <summary>Bytes com assinatura PNG real (o guard exige cabeçalho conhecido).</summary>
    private static byte[] ValidPngBytes() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static string ValidToolResult()
    {
        var bytes = ValidPngBytes();
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = true,
            ["mime"] = "image/png",
            ["image_base64"] = Convert.ToBase64String(bytes),
            ["bytes"] = bytes.Length,
            ["note"] = "Print da janela Bloco de Notas"
        });
    }

    [Test]
    public void BuildImagePartsFromToolResult_ExtractsTextAndImage()
    {
        var parts = AiChatHelpers.BuildImagePartsFromToolResult(ValidToolResult(), "capture_screenshot");

        Assert.That(parts, Is.Not.Null);
        Assert.That(parts!, Has.Count.EqualTo(2));
        Assert.That(parts![0].Type, Is.EqualTo("text"));
        Assert.That(parts![0].Text, Does.Contain("Bloco de Notas"));
        Assert.That(parts![1].Type, Is.EqualTo("image_url"));
        Assert.That(parts![1].ImageUrl, Does.StartWith("data:image/png;base64,"));
    }

    [Test]
    public void BuildImagePartsFromToolResult_WithoutImage_ReturnsNull()
    {
        Assert.That(AiChatHelpers.BuildImagePartsFromToolResult("{\"ok\":true}", "list_open_windows"), Is.Null);
        Assert.That(AiChatHelpers.BuildImagePartsFromToolResult(string.Empty, "capture_screenshot"), Is.Null);
        Assert.That(AiChatHelpers.BuildImagePartsFromToolResult("texto puro", "capture_screenshot"), Is.Null);
    }

    [Test]
    public void BuildImagePartsFromToolResult_InvalidJson_ReturnsNull()
    {
        Assert.That(AiChatHelpers.BuildImagePartsFromToolResult("{\"image_base64\": ", "capture_screenshot"), Is.Null);
    }

    [Test]
    public void BuildImagePartsFromToolResult_OversizedImage_ReturnsNull()
    {
        var huge = new string('A', AiChatHelpers.MaxImageBase64Chars + 1);
        var json = "{\"mime\":\"image/png\",\"image_base64\":\"" + huge + "\"}";

        Assert.That(AiChatHelpers.BuildImagePartsFromToolResult(json, "capture_screenshot"), Is.Null);
    }

    // Regressão do erro real (2026-10-09): o agent truncava o tool result em
    // 16 KB e cortava o base64. O provedor recusava o request inteiro com
    // "Provider stream error: Request could not be processed". O servidor agora
    // descarta o payload implausível e degrada para o caminho textual.
    [Test]
    public void BuildImagePartsFromToolResult_TruncatedBase64_ReturnsNull()
    {
        var full = ValidPngBytes();
        var truncated = Convert.ToBase64String(full)[..8]; // 8 chars = 6 bytes
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mime"] = "image/png",
            ["bytes"] = full.Length, // tamanho REAL declarado pelo agent
            ["image_base64"] = truncated
        });

        Assert.That(AiChatHelpers.BuildImagePartsFromToolResult(json, "capture_screenshot"), Is.Null);
    }

    [Test]
    public void BuildImagePartsFromToolResult_Base64InvalidLength_ReturnsNull()
    {
        // Corte de 1 byte no base64 (len % 4 != 0): decodificação impossível.
        var data = Convert.ToBase64String(ValidPngBytes());
        var json = "{\"mime\":\"image/png\",\"image_base64\":\"" + data[..^1] + "\"}";

        Assert.That(AiChatHelpers.BuildImagePartsFromToolResult(json, "capture_screenshot"), Is.Null);
    }

    [Test]
    public void BuildImagePartsFromToolResult_UnknownSignature_ReturnsNull()
    {
        var notAnImage = Convert.ToBase64String("isto nao e uma imagem!"u8.ToArray());
        var json = "{\"mime\":\"image/png\",\"image_base64\":\"" + notAnImage + "\"}";

        Assert.That(AiChatHelpers.BuildImagePartsFromToolResult(json, "capture_screenshot"), Is.Null);
    }

    [Test]
    public void BuildImagePartsFromToolResult_WithoutBytesField_StillExtracts()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mime"] = "image/png",
            ["image_base64"] = Convert.ToBase64String(ValidPngBytes())
        });

        var parts = AiChatHelpers.BuildImagePartsFromToolResult(json, "capture_screenshot");

        Assert.That(parts, Is.Not.Null, "sem o campo bytes a assinatura da imagem ja basta");
    }

    [Test]
    public void BuildImagePartsFromDataUrls_FiltersInvalidAndCapsCount()
    {
        var data = "data:image/png;base64," + Convert.ToBase64String(new byte[] { 1, 2, 3 });

        var parts = AiChatHelpers.BuildImagePartsFromDataUrls(
            new[] { "nao-e-data-url", string.Empty, data, data, data, data, data });

        Assert.That(parts, Is.Not.Null);
        Assert.That(parts!, Has.Count.EqualTo(AiChatHelpers.MaxImagesPerMessage));
        Assert.That(AiChatHelpers.BuildImagePartsFromDataUrls(new[] { "http://host/img.png" }), Is.Null);
        Assert.That(AiChatHelpers.BuildImagePartsFromDataUrls(null), Is.Null);
    }

    [Test]
    public void CompactToolResultForPersistence_RemovesBase64AndKeepsMetadata()
    {
        var compacted = AiChatHelpers.CompactToolResultForPersistence(ValidToolResult());

        Assert.That(compacted, Does.Not.Contain("AQID"));
        Assert.That(compacted, Does.Contain("image_omitted"));
        Assert.That(compacted, Does.Contain("Bloco de Notas"));
        Assert.That(compacted, Does.Contain("[omitido:"));
    }

    [Test]
    public void CompactToolResultForPersistence_LeavesOtherResultsUntouched()
    {
        Assert.That(AiChatHelpers.CompactToolResultForPersistence("{\"ok\":true,\"count\":3}"),
            Is.EqualTo("{\"ok\":true,\"count\":3}"));
        Assert.That(AiChatHelpers.CompactToolResultForPersistence("texto cru"), Is.EqualTo("texto cru"));
        Assert.That(AiChatHelpers.CompactToolResultForPersistence(string.Empty), Is.EqualTo(string.Empty));
    }

    // ── Guard de visão do modelo ────────────────────────────────────────────

    [Test]
    public void ResolveScreenshotImagesAllowed_HonoursSettingAndCapabilities()
    {
        static AiModelInfo Model(params string[] caps) => new(
            "m", "m", null, null, caps.ToList(), [], [], [], null, null, null, false, false, false, null, null);

        Assert.That(AiChatHelpers.ResolveScreenshotImagesAllowed(false, Model("vision")), Is.False);
        Assert.That(AiChatHelpers.ResolveScreenshotImagesAllowed(true, null), Is.True);
        Assert.That(AiChatHelpers.ResolveScreenshotImagesAllowed(true, Model()), Is.True);
        Assert.That(AiChatHelpers.ResolveScreenshotImagesAllowed(true, Model("chat", "tools")), Is.False);
        Assert.That(AiChatHelpers.ResolveScreenshotImagesAllowed(true, Model("chat", "VISION")), Is.True);
    }

    // ── Truncamento do tool result no controller ────────────────────────────

    [Test]
    public void TruncateToolResult_KeepsScreenshotImageIntact()
    {
        var image = "{\"ok\":true,\"mime\":\"image/png\",\"image_base64\":\"" + new string('A', 200_000) + "\"}";

        var kept = AgentAuthController.TruncateToolResult("capture_screenshot", image);

        Assert.That(kept, Is.EqualTo(image));
    }

    [Test]
    public void TruncateToolResult_StillTruncatesRegularTools()
    {
        var big = new string('x', 40_000);

        var truncated = AgentAuthController.TruncateToolResult("get_inventory", big);

        Assert.That(truncated.Length, Is.LessThan(big.Length));
    }
}