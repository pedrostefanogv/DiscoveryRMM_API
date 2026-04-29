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
}
