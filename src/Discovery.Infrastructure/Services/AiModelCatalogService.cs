using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Catálogo de modelos OpenRouter e OpenAI-compatible.
/// Cache em memória com TTL configurável (padrão 60 min), segmentado por provider + fingerprint de chave.
/// </summary>
public class AiModelCatalogService : IAiModelCatalogService
{
    private readonly HttpClient _httpClient;
    private readonly IAiCredentialResolver _credentialResolver;
    private readonly ILogger<AiModelCatalogService> _logger;

    // Cache: key = $"{provider}:{keyFingerprint}" → (timestamp, models)
    private readonly ConcurrentDictionary<string, (DateTime CachedAt, List<AiModelInfo> Models)> _cache = new();

    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(60);

    public AiModelCatalogService(
        IAiCredentialResolver credentialResolver,
        ILogger<AiModelCatalogService> logger)
    {
        _credentialResolver = credentialResolver;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public List<string> GetSupportedProviders() =>
    [
        AIIntegrationSettings.ProviderOpenAi,
        AIIntegrationSettings.ProviderOpenRouter,
        AIIntegrationSettings.ProviderOpenAiCompatible
    ];

    public async Task<AiModelCatalogResponse> ListModelsAsync(
        Guid? clientId,
        Guid? siteId,
        AiModelSearchRequest request,
        CancellationToken ct = default)
    {
        var resolved = await _credentialResolver.ResolveAsync(clientId, siteId, ct);
        var provider = !string.IsNullOrWhiteSpace(request.Provider) ? request.Provider : resolved?.Provider ?? "openai";

        var baseUrl = resolved?.BaseUrl ?? ResolveDefaultBaseUrl(provider);
        var apiKey = resolved?.ApiKey;

        var cacheKey = BuildCacheKey(provider, apiKey);

        // Cache hit?
        if (!request.ForceRefresh && _cache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow - cached.CachedAt < DefaultCacheTtl)
            {
                var filtered = FilterAndSearch(cached.Models, request);
                return new AiModelCatalogResponse(filtered, filtered.Count, provider, cached.CachedAt, true);
            }
        }

        // Fetch do provider
        List<AiModelInfo> models;
        try
        {
            models = provider.ToLowerInvariant() switch
            {
                AIIntegrationSettings.ProviderOpenRouter => await FetchOpenRouterModelsAsync(baseUrl, apiKey, ct),
                AIIntegrationSettings.ProviderOpenAi => await FetchOpenAiModelsAsync(baseUrl, apiKey, ct),
                _ => await FetchOpenAiCompatibleModelsAsync(baseUrl, apiKey, ct)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao buscar catálogo de modelos do provider {Provider}", provider);
            // Fallback: retorna cache expirado se existir
            if (_cache.TryGetValue(cacheKey, out var stale))
            {
                var filtered = FilterAndSearch(stale.Models, request);
                return new AiModelCatalogResponse(filtered, filtered.Count, provider, stale.CachedAt, true);
            }
            throw;
        }

        // Atualiza cache
        _cache[cacheKey] = (DateTime.UtcNow, models);

        var result = FilterAndSearch(models, request);
        return new AiModelCatalogResponse(result, result.Count, provider, DateTime.UtcNow, false);
    }

    public async Task<AiModelInfo?> GetModelAsync(
        Guid? clientId, Guid? siteId, string modelId, CancellationToken ct = default)
    {
        var response = await ListModelsAsync(clientId, siteId, new AiModelSearchRequest(), ct);
        return response.Models.FirstOrDefault(m =>
            m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AiModelValidationResult> ValidateModelAsync(
        Guid? clientId, Guid? siteId, string modelId, string? capability, CancellationToken ct = default)
    {
        var resolved = await _credentialResolver.ResolveAsync(clientId, siteId, ct);
        var apiKey = resolved?.ApiKey;
        var baseUrl = resolved?.BaseUrl ?? ResolveDefaultBaseUrl(resolved?.Provider ?? "openai");

        if (string.IsNullOrWhiteSpace(apiKey))
            return new AiModelValidationResult(false, modelId, "API key não configurada para este escopo.", null, null, null, null);

        try
        {
            // Validação leve: chama /models e verifica se modelId existe
            var response = await ListModelsAsync(clientId, siteId, new AiModelSearchRequest { ForceRefresh = true }, ct);
            var model = response.Models.FirstOrDefault(m =>
                m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));

            if (model is null)
                return new AiModelValidationResult(false, modelId, $"Modelo '{modelId}' não encontrado no catálogo do provider.", null, null, null, null);

            if (!string.IsNullOrWhiteSpace(model.IncompatibleReason))
                return new AiModelValidationResult(false, modelId, model.IncompatibleReason, model.EmbeddingDimensions, null, null, null);

            // Se pediu embedding, testa dimensão real
            int? embeddingDims = null;
            if (capability == "embeddings" && model.Capabilities.Contains("embeddings"))
            {
                embeddingDims = await TestEmbeddingDimensionsAsync(baseUrl, apiKey, modelId, ct);
            }

            var supportsTools = model.SupportedParameters.Contains("tools") || model.SupportedParameters.Contains("tool_choice");
            var supportsStreaming = model.Capabilities.Contains("streaming");

            return new AiModelValidationResult(true, modelId, "Modelo validado com sucesso.", embeddingDims, supportsTools, supportsStreaming, null);
        }
        catch (Exception ex)
        {
            return new AiModelValidationResult(false, modelId, $"Erro ao validar: {ex.Message}", null, null, null, null);
        }
    }

    // ─── Implementações específicas de provider ───

    private async Task<List<AiModelInfo>> FetchOpenRouterModelsAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");

        var models = new List<AiModelInfo>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var name = item.GetProperty("name").GetString() ?? id;
            var desc = item.TryGetProperty("description", out var d) ? d.GetString() : null;
            var contextLen = item.TryGetProperty("context_length", out var cl) ? cl.GetInt32() : (int?)null;

            var architecture = item.TryGetProperty("architecture", out var arch) ? arch : default;
            var inputMods = ParseStringArray(architecture, "input_modalities");
            var outputMods = ParseStringArray(architecture, "output_modalities");
            var supportedParams = ParseStringArray(item, "supported_parameters");

            var pricing = ParsePricing(item);

            var capabilities = DetermineCapabilities(inputMods, outputMods, supportedParams);
            var isFree = IsFreeModel(pricing);

            int? embeddingDims = DetermineEmbeddingDimensions(id, name, outputMods);

            var incompatible = DetermineIncompatibility(capabilities, outputMods, supportedParams);

            models.Add(new AiModelInfo(
                Id: id,
                Name: name,
                Description: desc,
                Provider: "openrouter",
                Capabilities: capabilities,
                InputModalities: inputMods,
                OutputModalities: outputMods,
                SupportedParameters: supportedParams,
                ContextLength: contextLen,
                MaxCompletionTokens: item.TryGetProperty("max_completion_tokens", out var mct) ? mct.GetInt32() : null,
                Pricing: pricing,
                IsFree: isFree,
                IsRecommendedForChat: IsRecommendedForChat(id, capabilities, isFree),
                IsRecommendedForEmbedding: outputMods.Contains("embeddings", StringComparer.OrdinalIgnoreCase),
                EmbeddingDimensions: embeddingDims,
                IncompatibleReason: incompatible
            ));
        }

        return models;
    }

    private async Task<List<AiModelInfo>> FetchOpenAiModelsAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");

        var models = new List<AiModelInfo>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var ownedBy = item.TryGetProperty("owned_by", out var ob) ? ob.GetString() : null;

            // OpenAI expõe só id + owned_by — inferimos capacidades pelo id
            var capabilities = InferOpenAiCapabilities(id);
            var outputMods = capabilities.Contains("embeddings")
                ? new List<string> { "embeddings" }
                : new List<string> { "text" };

            var embeddingDims = DetermineEmbeddingDimensions(id, id, outputMods);

            models.Add(new AiModelInfo(
                Id: id,
                Name: id,
                Description: ownedBy,
                Provider: "openai",
                Capabilities: capabilities,
                InputModalities: new List<string> { "text" },
                OutputModalities: outputMods,
                SupportedParameters: capabilities.Contains("tools") ? new List<string> { "tools", "tool_choice" } : [],
                ContextLength: GetOpenAiContextLength(id),
                MaxCompletionTokens: null,
                Pricing: null,
                IsFree: false,
                IsRecommendedForChat: capabilities.Contains("chat") && !capabilities.Contains("embeddings"),
                IsRecommendedForEmbedding: capabilities.Contains("embeddings"),
                EmbeddingDimensions: embeddingDims,
                IncompatibleReason: null
            ));
        }

        return models;
    }

    private async Task<List<AiModelInfo>> FetchOpenAiCompatibleModelsAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        // Tenta /models primeiro
        try
        {
            return await FetchOpenAiModelsAsync(baseUrl, apiKey, ct);
        }
        catch
        {
            // Fallback: lista estática de modelos conhecidos compatíveis
            _logger.LogWarning("Provider genérico não expõe /models — usando fallback manual");
            return GetFallbackModels();
        }
    }

    private async Task<int?> TestEmbeddingDimensionsAsync(string baseUrl, string apiKey, string modelId, CancellationToken ct)
    {
        try
        {
            var requestBody = JsonSerializer.Serialize(new { model = modelId, input = "test" });
            var url = $"{baseUrl.TrimEnd('/')}/embeddings";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("data")[0].GetProperty("embedding").GetArrayLength();
        }
        catch
        {
            return null;
        }
    }

    // ─── Helpers ───

    private static List<string> ParseStringArray(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind == JsonValueKind.Undefined) return [];
        if (!parent.TryGetProperty(propertyName, out var arr)) return [];
        if (arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private static AiModelPricing? ParsePricing(JsonElement item)
    {
        if (!item.TryGetProperty("pricing", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        return new AiModelPricing(
            PromptPerMillion: ParseDecimal(p, "prompt"),
            CompletionPerMillion: ParseDecimal(p, "completion"),
            ImagePerMillion: ParseDecimal(p, "image")
        );
    }

    private static decimal? ParseDecimal(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var v) && v.TryGetDecimal(out var d) ? d : null;

    private static List<string> DetermineCapabilities(List<string> inputMods, List<string> outputMods, List<string> supportedParams)
    {
        var caps = new List<string>();
        if (outputMods.Contains("text", StringComparer.OrdinalIgnoreCase)) caps.Add("chat");
        if (outputMods.Contains("embeddings", StringComparer.OrdinalIgnoreCase)) caps.Add("embeddings");
        if (supportedParams.Any(p => p.Contains("tool", StringComparison.OrdinalIgnoreCase))) caps.Add("tools");
        if (inputMods.Contains("image", StringComparer.OrdinalIgnoreCase) || inputMods.Contains("image_url", StringComparer.OrdinalIgnoreCase)) caps.Add("vision");
        if (inputMods.Contains("audio", StringComparer.OrdinalIgnoreCase)) caps.Add("audio");
        caps.Add("streaming");
        return caps;
    }

    private static List<string> InferOpenAiCapabilities(string id)
    {
        var caps = new List<string>();
        if (id.StartsWith("gpt-") || id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4"))
        {
            caps.Add("chat");
            caps.Add("streaming");
            if (!id.Contains("mini")) caps.Add("tools");
        }
        else if (id.Contains("embedding") || id.Contains("embed"))
        {
            caps.Add("embeddings");
        }
        return caps;
    }

    private static int? GetOpenAiContextLength(string id) => id switch
    {
        string s when s.StartsWith("gpt-4-turbo") || s.StartsWith("gpt-4o") => 128000,
        string s when s.StartsWith("gpt-4") => 8192,
        string s when s.StartsWith("gpt-3.5-turbo") => 16385,
        string s when s.StartsWith("o1") => 200000,
        string s when s.StartsWith("o3") => 200000,
        string s when s.StartsWith("o4") => 200000,
        _ => null
    };

    private static bool IsFreeModel(AiModelPricing? pricing)
    {
        if (pricing is null) return false;
        return (pricing.PromptPerMillion ?? 0) == 0 && (pricing.CompletionPerMillion ?? 0) == 0;
    }

    private static int? DetermineEmbeddingDimensions(string id, string name, List<string> outputMods)
    {
        if (!outputMods.Contains("embeddings", StringComparer.OrdinalIgnoreCase)) return null;

        var combined = $"{id} {name}".ToLowerInvariant();

        if (combined.Contains("text-embedding-3-large")) return 3072;
        if (combined.Contains("text-embedding-3-small")) return 1536;
        if (combined.Contains("text-embedding-ada")) return 1536;
        if (combined.Contains("qwen3-embedding") || combined.Contains("qwen/qwen3-embedding")) return 1536;
        if (combined.Contains("jina-embeddings")) return 1024;
        if (combined.Contains("nomic-embed-text")) return 768;
        if (combined.Contains("bge-") || combined.Contains("bge-large")) return 1024;

        return null; // desconhecido — usuário deve validar
    }

    private static bool IsRecommendedForChat(string id, List<string> capabilities, bool isFree)
    {
        if (!capabilities.Contains("chat")) return false;
        return isFree || id.Contains("gpt-4o-mini") || id.Contains("claude") || id.Contains("gemini-flash");
    }

    private static string? DetermineIncompatibility(List<string> capabilities, List<string> outputMods, List<string> supportedParams)
    {
        if (outputMods.Count == 0) return "Sem output modalities definidas";
        if (!outputMods.Contains("text", StringComparer.OrdinalIgnoreCase) &&
            !outputMods.Contains("embeddings", StringComparer.OrdinalIgnoreCase))
            return "Modelo não gera texto nem embeddings";
        return null;
    }

    private static List<AiModelInfo> FilterAndSearch(List<AiModelInfo> models, AiModelSearchRequest request)
    {
        var query = models.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(request.Capability))
        {
            var cap = request.Capability.ToLowerInvariant();
            query = query.Where(m => m.Capabilities.Any(c => c.Equals(cap, StringComparison.OrdinalIgnoreCase)));
        }

        if (request.FreeOnly)
            query = query.Where(m => m.IsFree);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.ToLowerInvariant();
            query = query.Where(m =>
                m.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (m.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.Provider?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return query.ToList();
    }

    private static string BuildCacheKey(string provider, string? apiKey)
    {
        var fp = string.IsNullOrWhiteSpace(apiKey) ? "nokey" : ComputeSimpleHash(apiKey);
        return $"{provider.ToLowerInvariant()}:{fp}";
    }

    private static string ComputeSimpleHash(string input)
    {
        // Hash rápido (não criptográfico) para particionar cache sem expor chave
        unchecked
        {
            int hash = 17;
            foreach (var c in input) hash = hash * 31 + c;
            return Math.Abs(hash).ToString("x8");
        }
    }

    private static string ResolveDefaultBaseUrl(string provider) => provider.ToLowerInvariant() switch
    {
        AIIntegrationSettings.ProviderOpenRouter => AIIntegrationSettings.OpenRouterDefaultBaseUrl,
        _ => AIIntegrationSettings.OpenAiDefaultBaseUrl
    };

    private static List<AiModelInfo> GetFallbackModels() =>
    [
        new AiModelInfo("gpt-4o-mini", "GPT-4o Mini", "OpenAI", "openai",
            ["chat", "streaming", "tools"], ["text"], ["text"], ["tools", "tool_choice"], 128000, 16384, null, false, true, false, null, null),
        new AiModelInfo("gpt-4o", "GPT-4o", "OpenAI", "openai",
            ["chat", "streaming", "tools", "vision"], ["text", "image"], ["text"], ["tools", "tool_choice"], 128000, 16384, null, false, false, false, null, null),
        new AiModelInfo("text-embedding-3-small", "Text Embedding 3 Small", "OpenAI", "openai",
            ["embeddings"], ["text"], ["embeddings"], [], null, null, null, false, false, true, 1536, null),
        new AiModelInfo("text-embedding-3-large", "Text Embedding 3 Large", "OpenAI", "openai",
            ["embeddings"], ["text"], ["embeddings"], [], null, null, null, false, false, true, 3072, null),
    ];
}
