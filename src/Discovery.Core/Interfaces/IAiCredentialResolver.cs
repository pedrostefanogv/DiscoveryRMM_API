namespace Discovery.Core.Interfaces;

/// <summary>
/// Resolve credenciais AI com herança hierárquica: Site → Client → Global.
/// Ver implementação em Discovery.Infrastructure.Services.AiCredentialResolver.
/// </summary>
public interface IAiCredentialResolver
{
    /// <summary>Resolve credencial efetiva (chave descriptografada) para o escopo</summary>
    Task<ResolvedCredential?> ResolveAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default);

    /// <summary>Resolve apenas a chave de embedding efetiva</summary>
    Task<string?> ResolveEmbeddingApiKeyAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default);

    /// <summary>Resolve apenas a chave de chat efetiva</summary>
    Task<string?> ResolveChatApiKeyAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default);
}

/// <summary>
/// Credencial resolvida com herança, pronta para uso (chaves descriptografadas).
/// </summary>
public class ResolvedCredential
{
    public string Provider { get; init; } = "openai";
    public string? BaseUrl { get; init; }
    public string? EmbeddingBaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? EmbeddingApiKey { get; init; }
    public Discovery.Core.Enums.AppApprovalScopeType SourceScope { get; init; }
    public Guid CredentialId { get; init; }

    public string? EffectiveEmbeddingApiKey => !string.IsNullOrWhiteSpace(EmbeddingApiKey) ? EmbeddingApiKey : ApiKey;
    public string? EffectiveEmbeddingBaseUrl => !string.IsNullOrWhiteSpace(EmbeddingBaseUrl) ? EmbeddingBaseUrl : BaseUrl;
}
