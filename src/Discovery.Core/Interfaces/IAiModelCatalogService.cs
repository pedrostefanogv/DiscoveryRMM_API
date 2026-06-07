using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Catálogo de modelos AI disponíveis via API do provider (OpenRouter / OpenAI / compatível).
/// </summary>
public interface IAiModelCatalogService
{
    /// <summary>
    /// Lista modelos disponíveis com filtros e cache.
    /// </summary>
    Task<AiModelCatalogResponse> ListModelsAsync(
        Guid? clientId,
        Guid? siteId,
        AiModelSearchRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Obtém detalhes de um modelo específico.
    /// </summary>
    Task<AiModelInfo?> GetModelAsync(
        Guid? clientId,
        Guid? siteId,
        string modelId,
        CancellationToken ct = default);

    /// <summary>
    /// Valida um modelo (testa conectividade, verifica capacidades).
    /// </summary>
    Task<AiModelValidationResult> ValidateModelAsync(
        Guid? clientId,
        Guid? siteId,
        string modelId,
        string? capability = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lista providers suportados (ex: ["openai", "openrouter", "openai-compatible"]).
    /// </summary>
    List<string> GetSupportedProviders();

    /// <summary>
    /// Busca modelos diretamente da API OpenRouter (/models e /embeddings/models) com cache de 60min.
    /// </summary>
    Task<OpenRouterModelsResponse> ListOpenRouterModelsAsync(
        string? modality = null,
        bool forceRefresh = false,
        CancellationToken ct = default);

    /// <summary>
    /// Valida uma API key contra o provider (faz uma requisição leve de ping).
    /// </summary>
    Task<bool> ValidateApiKeyAsync(
        string provider,
        string baseUrl,
        string apiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Rerank documents usando cross-encoder via OpenRouter (/rerank).
    /// </summary>
    Task<List<AiRerankResult>> RerankAsync(
        string query,
        List<string> documents,
        string? model = null,
        int? topN = null,
        CancellationToken ct = default);
}
