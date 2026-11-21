using System.Text.Json;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressão do bug que derrubava o catálogo de modelos do OpenRouter: a API
/// devolve `pricing` como STRING (ex.: "0.0000012") e JsonElement.TryGetDecimal
/// LANÇA InvalidOperationException nesse caso. A exceção abortava o fetch do
/// catálogo inteiro; sem catálogo, o guard de visão do chat enviava prints para
/// modelos roteados sem visão e o provedor respondia
/// "Provider stream error: Request could not be processed".
/// </summary>
public class AiModelCatalogPricingTests
{
    [Test]
    public void ParsePricing_AceitaPrecosComoString()
    {
        using var doc = JsonDocument.Parse("""
            {"pricing":{"prompt":"0.0000012","completion":"0.000004","image":"0"}}
            """);

        var pricing = AiModelCatalogService.ParsePricing(doc.RootElement);

        Assert.That(pricing, Is.Not.Null);
        Assert.That(pricing!.PromptPerMillion, Is.EqualTo(0.0000012m));
        Assert.That(pricing.CompletionPerMillion, Is.EqualTo(0.000004m));
        Assert.That(pricing.ImagePerMillion, Is.EqualTo(0m));
    }

    [Test]
    public void ParsePricing_AceitaPrecosComoNumero()
    {
        using var doc = JsonDocument.Parse("""{"pricing":{"prompt":1.5,"completion":2}}""");

        var pricing = AiModelCatalogService.ParsePricing(doc.RootElement);

        Assert.That(pricing, Is.Not.Null);
        Assert.That(pricing!.PromptPerMillion, Is.EqualTo(1.5m));
        Assert.That(pricing.CompletionPerMillion, Is.EqualTo(2m));
        Assert.That(pricing.ImagePerMillion, Is.Null);
    }

    [Test]
    public void ParsePricing_NaoLancaComTiposInesperados()
    {
        using var doc = JsonDocument.Parse("""{"pricing":{"prompt":{},"completion":true,"image":[]}}""");

        var pricing = AiModelCatalogService.ParsePricing(doc.RootElement);

        Assert.That(pricing, Is.Not.Null, "um item fora do formato nao pode derrubar o catalogo");
        Assert.That(pricing!.PromptPerMillion, Is.Null);
        Assert.That(pricing.CompletionPerMillion, Is.Null);
        Assert.That(pricing.ImagePerMillion, Is.Null);
    }

    [Test]
    public void ParsePricing_SemObjetoPricingDevolveNull()
    {
        using var doc = JsonDocument.Parse("""{"id":"openrouter/auto"}""");
        Assert.That(AiModelCatalogService.ParsePricing(doc.RootElement), Is.Null);
    }

    [Test]
    public void ParseInt32_AceitaNumeroStringAusenteENulo()
    {
        using var doc = JsonDocument.Parse("""{"context_length":128000,"max_completion_tokens":"8192","nulo":null}""");
        var root = doc.RootElement;

        Assert.That(AiModelCatalogService.ParseInt32(root, "context_length"), Is.EqualTo(128000));
        Assert.That(AiModelCatalogService.ParseInt32(root, "max_completion_tokens"), Is.EqualTo(8192));
        Assert.That(AiModelCatalogService.ParseInt32(root, "ausente"), Is.Null);
        Assert.That(AiModelCatalogService.ParseInt32(root, "nulo"), Is.Null);
    }

    [Test]
    public void InferOpenAiCapabilities_SupportsMiniToolsAndVisionModels()
    {
        var miniCaps = AiModelCatalogService.InferOpenAiCapabilities("gpt-4o-mini");
        Assert.That(miniCaps, Does.Contain("tools"));
        Assert.That(miniCaps, Does.Contain("vision"));

        var gpt4oCaps = AiModelCatalogService.InferOpenAiCapabilities("gpt-4o");
        Assert.That(gpt4oCaps, Does.Contain("tools"));
        Assert.That(gpt4oCaps, Does.Contain("vision"));

        var o3MiniCaps = AiModelCatalogService.InferOpenAiCapabilities("o3-mini");
        Assert.That(o3MiniCaps, Does.Contain("tools"));
        Assert.That(o3MiniCaps, Does.Not.Contain("vision"));
    }

    [Test]
    public void OpenAiProvider_BuildChatPayload_FormatsResponseFormatAndStreamOptions()
    {
        var opts = new Discovery.Core.Interfaces.LlmOptions(
            MaxTokens: 1024,
            Temperature: 0.2,
            Model: "gpt-4o",
            ResponseFormat: "json_object",
            Provider: "openai");

        var payload = OpenAiProvider.BuildChatPayload(opts, [], includeTools: false, stream: true);

        Assert.That(payload["stream"], Is.EqualTo(true));
        var streamOpts = payload["stream_options"] as Dictionary<string, object>;
        Assert.That(streamOpts, Is.Not.Null);
        Assert.That(streamOpts!["include_usage"], Is.EqualTo(true));

        var respFmt = payload["response_format"] as Dictionary<string, object>;
        Assert.That(respFmt, Is.Not.Null);
        Assert.That(respFmt!["type"], Is.EqualTo("json_object"));
    }
}
