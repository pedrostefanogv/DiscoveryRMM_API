namespace Discovery.Core.DTOs;

/// <summary>
/// Modelo AI retornado pelo catálogo (OpenRouter / OpenAI / genérico).
/// </summary>
public record AiModelInfo(
    string Id,
    string Name,
    string? Description,
    string? Provider,
    List<string> Capabilities,
    List<string> InputModalities,
    List<string> OutputModalities,
    List<string> SupportedParameters,
    int? ContextLength,
    int? MaxCompletionTokens,
    AiModelPricing? Pricing,
    bool IsFree,
    bool IsRecommendedForChat,
    bool IsRecommendedForEmbedding,
    int? EmbeddingDimensions,
    string? IncompatibleReason
);

/// <summary>
/// Preço do modelo (por 1M tokens).
/// </summary>
public record AiModelPricing(
    decimal? PromptPerMillion,
    decimal? CompletionPerMillion,
    decimal? ImagePerMillion
);

/// <summary>
/// Parâmetros de busca/listagem de modelos.
/// </summary>
public record AiModelSearchRequest(
    string? Provider = null,
    string? Capability = null,  // chat, embeddings, tools, vision, audio
    string? Search = null,
    bool ForceRefresh = false,
    bool FreeOnly = false
);

/// <summary>
/// Resultado de validação de modelo.
/// </summary>
public record AiModelValidationResult(
    bool IsValid,
    string? ModelId,
    string? Message,
    int? EmbeddingDimensions,
    bool? SupportsTools,
    bool? SupportsStreaming,
    long? LatencyMs
);

/// <summary>
/// Resposta paginada do catálogo de modelos.
/// </summary>
public record AiModelCatalogResponse(
    List<AiModelInfo> Models,
    int TotalCount,
    string Provider,
    DateTime? CachedAt,
    bool FromCache
);

/// <summary>
/// Request para validação de modelo AI.
/// </summary>
public record AiModelValidationRequest(
    string ModelId,
    string? Capability = null,
    Guid? ClientId = null,
    Guid? SiteId = null
);
