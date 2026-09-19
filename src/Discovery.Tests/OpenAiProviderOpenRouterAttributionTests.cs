using System.Net.Http;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Atribuição do app nos logs do OpenRouter: TODA requisição que atinge
/// openrouter.ai (por provider configurado OU por baseUrl de credencial por
/// escopo) deve enviar HTTP-Referer/X-Title — caso contrário o OpenRouter
/// registra o uso como "Unknown" no painel de atividade.
/// </summary>
public class OpenAiProviderOpenRouterAttributionTests
{
    private const string RefererDefault = "https://discovery-rmm.local";
    private const string TitleDefault = "Discovery RMM";

    private static HttpRequestMessage NewRequest() =>
        new(HttpMethod.Post, "https://exemplo.invalid/chat/completions");

    // ── AutoCorrectProviderAndBaseUrl ──────────────────────────────────────

    [Test]
    public void AutoCorrect_BaseUrlOpenRouter_ComModeloSemSlash_TrataComoOpenRouter()
    {
        // Credencial por escopo: Provider="openai" (default da entidade) +
        // BaseUrl OpenRouter + modelo sem "/" (ex: gpt-oss-120b sem prefixo).
        var (provider, baseUrl) = OpenAiProvider.AutoCorrectProviderAndBaseUrl(
            "openai", "https://openrouter.ai/api/v1/", "gpt-oss-120b");

        Assert.Multiple(() =>
        {
            Assert.That(provider, Is.EqualTo(AIIntegrationSettings.ProviderOpenRouter));
            Assert.That(baseUrl, Is.EqualTo("https://openrouter.ai/api/v1/"));
        });
    }

    [Test]
    public void AutoCorrect_ModeloComSlash_MesmoComBaseUrlOpenAI_TrataComoOpenRouter()
    {
        // Modelo OpenRouter sempre ganha provider openrouter (comportamento pré-existente).
        var (provider, _) = OpenAiProvider.AutoCorrectProviderAndBaseUrl(
            "openai", AIIntegrationSettings.OpenAiDefaultBaseUrl, "openai/gpt-oss-120b");

        Assert.That(provider, Is.EqualTo(AIIntegrationSettings.ProviderOpenRouter));
    }

    [Test]
    public void AutoCorrect_ProviderOpenAI_BaseUrlOpenAI_NaoAltera()
    {
        var (provider, baseUrl) = OpenAiProvider.AutoCorrectProviderAndBaseUrl(
            "openai", null, "gpt-4o-mini");

        Assert.Multiple(() =>
        {
            Assert.That(provider, Is.EqualTo("openai"));
            Assert.That(baseUrl, Is.EqualTo(AIIntegrationSettings.OpenAiDefaultBaseUrl));
        });
    }

    // ── ApplyOpenRouterHeaders ─────────────────────────────────────────────

    [Test]
    public void ApplyHeaders_BaseUrlOpenRouter_EnviaHeadersMesmoComProviderDiferente()
    {
        // Caso do bug: chamadas de chat que atingiam openrouter.ai com
        // Provider != "openrouter" iam sem HTTP-Referer/X-Title ("Unknown").
        var request = NewRequest();
        var options = new LlmOptions(Provider: "openai");

        OpenAiProvider.ApplyOpenRouterHeaders(request, options, "https://openrouter.ai/api/v1/");

        Assert.Multiple(() =>
        {
            Assert.That(request.Headers.GetValues("HTTP-Referer"), Does.Contain(RefererDefault));
            Assert.That(request.Headers.GetValues("X-Title"), Does.Contain(TitleDefault));
        });
    }

    [Test]
    public void ApplyHeaders_ProviderOpenRouter_EnviaHeadersComFallback()
    {
        var request = NewRequest();
        var options = new LlmOptions(Provider: "openrouter");

        OpenAiProvider.ApplyOpenRouterHeaders(request, options, AIIntegrationSettings.OpenRouterDefaultBaseUrl);

        Assert.Multiple(() =>
        {
            Assert.That(request.Headers.GetValues("HTTP-Referer"), Does.Contain(RefererDefault));
            Assert.That(request.Headers.GetValues("X-Title"), Does.Contain(TitleDefault));
        });
    }

    [Test]
    public void ApplyHeaders_ProviderOpenAI_BaseUrlOpenAI_NaoEnviaHeadersOpenRouter()
    {
        var request = NewRequest();
        var options = new LlmOptions(Provider: "openai");

        OpenAiProvider.ApplyOpenRouterHeaders(request, options, AIIntegrationSettings.OpenAiDefaultBaseUrl);

        Assert.Multiple(() =>
        {
            Assert.That(request.Headers.Contains("HTTP-Referer"), Is.False);
            Assert.That(request.Headers.Contains("X-Title"), Is.False);
        });
    }

    [Test]
    public void ApplyHeaders_RefererTitleConfigurados_TemPreferenciaSobreFallback()
    {
        var request = NewRequest();
        var options = new LlmOptions(
            Provider: "openrouter",
            OpenRouterReferer: "https://meuapp.exemplo",
            OpenRouterTitle: "Meu App");

        OpenAiProvider.ApplyOpenRouterHeaders(request, options, AIIntegrationSettings.OpenRouterDefaultBaseUrl);

        Assert.Multiple(() =>
        {
            Assert.That(request.Headers.GetValues("HTTP-Referer"), Does.Contain("https://meuapp.exemplo"));
            Assert.That(request.Headers.GetValues("X-Title"), Does.Contain("Meu App"));
        });
    }
}
