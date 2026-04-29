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

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiEmbeddingProvider> _logger;

    public OpenAiEmbeddingProvider(ILogger<OpenAiEmbeddingProvider> logger)
    {
        _logger = logger;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>Aplica headers OpenRouter se a base URL for OpenRouter</summary>
    private static void ApplyOpenRouterHeaders(HttpRequestMessage request, string? baseUrl, AIIntegrationSettings? settings)
    {
        if (settings is null) return;
        if (baseUrl is null || !baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.IsNullOrWhiteSpace(settings.OpenRouterReferer))
            request.Headers.TryAddWithoutValidation("HTTP-Referer", settings.OpenRouterReferer);

        if (!string.IsNullOrWhiteSpace(settings.OpenRouterTitle))
            request.Headers.TryAddWithoutValidation("X-Title", settings.OpenRouterTitle);
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

        var resolvedBaseUrl = !string.IsNullOrWhiteSpace(baseUrlOverride)
            ? baseUrlOverride
            : AIIntegrationSettings.OpenAiDefaultBaseUrl;

        var requestUri = new Uri(new Uri(resolvedBaseUrl.TrimEnd('/') + '/'), "embeddings");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyOverride);

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Embeddings API erro {Status}: {Body}", response.StatusCode, errorBody);
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

        var resolvedBaseUrl = !string.IsNullOrWhiteSpace(baseUrlOverride)
            ? baseUrlOverride
            : AIIntegrationSettings.OpenAiDefaultBaseUrl;

        var requestUri = new Uri(new Uri(resolvedBaseUrl.TrimEnd('/') + '/'), "embeddings");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyOverride);

        // Aplica headers OpenRouter se for o provider
        if (resolvedBaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
        {
            // Não temos settings aqui, mas a URL indica OpenRouter
            _logger.LogDebug("Batch embedding para OpenRouter detectado pela URL");
        }

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Embeddings batch API erro {Status}: {Body}", response.StatusCode, errorBody);
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
