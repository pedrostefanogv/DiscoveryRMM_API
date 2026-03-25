using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Gera embeddings via OpenAI text-embedding-3-small (1536 dims).
/// Reutiliza a chave OpenAI:ApiKey já configurada no appsettings.
/// </summary>
public class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private const int MaxInputChars = 30000; // ~8000 tokens de segurança
    private const string DefaultEmbeddingModel = "text-embedding-3-small";
    private const string DefaultBaseUrl = "https://api.openai.com/v1/";

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiEmbeddingProvider> _logger;

    public OpenAiEmbeddingProvider(ILogger<OpenAiEmbeddingProvider> logger)
    {
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(DefaultBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
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

        var resolvedBaseUrl = string.IsNullOrWhiteSpace(baseUrlOverride) ? DefaultBaseUrl : baseUrlOverride;
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
            _logger.LogError("OpenAI Embeddings API erro {Status}: {Body}", response.StatusCode, errorBody);
            throw new HttpRequestException($"OpenAI Embeddings falhou: {response.StatusCode}");
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
}
