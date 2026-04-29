namespace Discovery.Core.DTOs;

/// <summary>
/// DTO público para credenciais AI — nunca expõe chaves brutas.
/// </summary>
public record AiProviderCredentialDto(
    Guid Id,
    string ScopeType,
    Guid? ClientId,
    Guid? SiteId,
    string Provider,
    string? BaseUrl,
    string? EmbeddingBaseUrl,
    bool HasApiKey,
    bool HasEmbeddingApiKey,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy
);

/// <summary>
/// Request para criar/atualizar credencial AI.
/// </summary>
public record AiProviderCredentialUpsertRequest(
    string ScopeType,
    Guid? ClientId,
    Guid? SiteId,
    string Provider,
    string? BaseUrl,
    string? EmbeddingBaseUrl,
    string? ApiKey,
    string? EmbeddingApiKey
);

/// <summary>
/// Resultado do teste de conexão com o provider.
/// </summary>
public record AiProviderTestResult(
    bool Success,
    string? Message,
    string? ModelTested,
    int? EmbeddingDimensions,
    long? LatencyMs
);
