using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Gera embeddings via API compatível com OpenAI (OpenAI, OpenRouter, genérico).
/// Suporta batching quando o provider aceita input como array de strings.
/// </summary>
public class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private const int MaxInputChars = 30000; // ~8000 tokens de segurança
    private const string DefaultEmbeddingModel = "text-embedding-3-small";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiEmbeddingProvider> _logger;

    public OpenAiEmbeddingProvider(IHttpClientFactory httpClientFactory, ILogger<OpenAiEmbeddingProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Aplica headers OpenRouter se a base URL for OpenRouter</summary>
    private static void ApplyOpenRouterHeaders(HttpRequestMessage request, string? baseUrl)
    {
        if (baseUrl is null || !baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
            return;

        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://discovery-rmm.local");
        request.Headers.TryAddWithoutValidation("X-Title", "Discovery RMM");
    }

    /// <summary>
    /// Resolve a baseUrl efetiva dos embeddings, aplicando a mesma invariante do
    /// chat (<see cref="OpenAiProvider.AutoCorrectProviderAndBaseUrl"/>): uma
    /// credencial OpenRouter NUNCA deve ser enviada para o host de outro provedor.
    ///
    /// A detecção do OpenRouter é feita pela própria credencial/baseUrl, porque a
    /// assinatura de IEmbeddingProvider não carrega o provider configurado:
    ///   - apiKey com prefixo "sk-or-" (chave OpenRouter) => OpenRouter;
    ///   - baseUrl contendo openrouter.ai            => OpenRouter.
    ///
    /// Nesses casos, uma baseUrl apontando para api.openai.com (ou vazia) é
    /// substituída pelo default do OpenRouter. Customizações legítimas
    /// (proxy self-hosted, gateway interno, openrouter.ai) são preservadas.
    /// </summary>
    internal static string ResolveEmbeddingBaseUrl(string? baseUrlOverride, string? apiKey)
    {
        var isOpenRouterCredential = !string.IsNullOrWhiteSpace(apiKey)
            && apiKey.StartsWith("sk-or-", StringComparison.OrdinalIgnoreCase);

        var isOpenRouterBaseUrl = !string.IsNullOrWhiteSpace(baseUrlOverride)
            && baseUrlOverride.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);

        var isOpenRouter = isOpenRouterCredential || isOpenRouterBaseUrl;

        if (!string.IsNullOrWhiteSpace(baseUrlOverride))
        {
            // baseUrl de outro provedor conhecido + credencial OpenRouter => inconsistente.
            if (isOpenRouter && !OpenAiProvider.IsUsableOpenRouterBaseUrl(baseUrlOverride))
                return AIIntegrationSettings.OpenRouterDefaultBaseUrl;

            return baseUrlOverride;
        }

        return isOpenRouter
            ? AIIntegrationSettings.OpenRouterDefaultBaseUrl
            : AIIntegrationSettings.OpenAiDefaultBaseUrl;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, string? modelOverride = null, string? apiKeyOverride = null, string? baseUrlOverride = null, CancellationToken ct = default)
    {
        var embeddingModel = string.IsNullOrWhiteSpace(modelOverride)
            ? DefaultEmbeddingModel
            : modelOverride;

        if (string.IsNullOrWhiteSpace(apiKeyOverride))
            throw new InvalidOperationException("API key de IA não definida no banco para geração de embeddings.");

        // Trunca texto se muito longo (segurança)
        var input = text.Length > MaxInputChars ? text[..MaxInputChars] : text;

        var requestBody = JsonSerializer.Serialize(new
        {
            model = embeddingModel,
            input
        });

        var resolvedBaseUrl = ResolveEmbeddingBaseUrl(baseUrlOverride, apiKeyOverride);

        var requestUri = new Uri(new Uri(resolvedBaseUrl.TrimEnd('/') + '/'), "embeddings");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyOverride);
        ApplyOpenRouterHeaders(request, resolvedBaseUrl);

        var httpClient = _httpClientFactory.CreateClient("AiChat");
        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Embeddings API erro {Status} em {Host}: {Body}",
                response.StatusCode, requestUri.Host, errorBody);
            throw new HttpRequestException($"Embeddings falhou: {response.StatusCode}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseBody);

        var embeddingArray = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(v => v.GetSingle())
            .ToArray();

        _logger.LogDebug("Embedding gerado: {Dims} dimensões para texto de {Chars} chars",
            embeddingArray.Length, input.Length);

        return embeddingArray;
    }

    /// <summary>
    /// Gera embeddings em batch usando input como array de strings (suportado por OpenAI e OpenRouter).
    /// Muito mais eficiente que chamadas individuais — reduz latência e custo.
    /// </summary>
    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> inputs,
        string? modelOverride = null,
        string? apiKeyOverride = null,
        string? baseUrlOverride = null,
        CancellationToken ct = default)
    {
        if (inputs.Count == 0)
            return Array.Empty<float[]>();

        if (inputs.Count == 1)
        {
            var single = await GenerateEmbeddingAsync(inputs[0], modelOverride, apiKeyOverride, baseUrlOverride, ct);
            return new[] { single };
        }

        var embeddingModel = string.IsNullOrWhiteSpace(modelOverride)
            ? DefaultEmbeddingModel
            : modelOverride;

        if (string.IsNullOrWhiteSpace(apiKeyOverride))
            throw new InvalidOperationException("API key de IA não definida no banco para geração de embeddings.");

        var truncated = inputs
            .Select(t => t.Length > MaxInputChars ? t[..MaxInputChars] : t)
            .ToList();

        var requestBody = JsonSerializer.Serialize(new
        {
            model = embeddingModel,
            input = truncated
        });

        var resolvedBaseUrl = ResolveEmbeddingBaseUrl(baseUrlOverride, apiKeyOverride);

        var requestUri = new Uri(new Uri(resolvedBaseUrl.TrimEnd('/') + '/'), "embeddings");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyOverride);
        ApplyOpenRouterHeaders(request, resolvedBaseUrl);

        var httpClient2 = _httpClientFactory.CreateClient("AiChat");
        var response = await httpClient2.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Embeddings batch API erro {Status} em {Host}: {Body}",
                response.StatusCode, requestUri.Host, errorBody);
            throw new HttpRequestException($"Embeddings batch falhou: {response.StatusCode}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseBody);

        var dataArray = doc.RootElement.GetProperty("data");
        var results = new List<float[]>(dataArray.GetArrayLength());

        foreach (var item in dataArray.EnumerateArray())
        {
            var emb = item.GetProperty("embedding")
                .EnumerateArray()
                .Select(v => v.GetSingle())
                .ToArray();
            results.Add(emb);
        }

        _logger.LogDebug("Embeddings batch: {Count} vetores de {Dims} dimensões para {InputCount} inputs",
            results.Count, results.Count > 0 ? results[0].Length : 0, truncated.Count);

        return results;
    }
}
