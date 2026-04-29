using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Entities;

/// <summary>
/// Credencial de provider AI por escopo (Server, Client, Site).
/// As chaves são criptografadas em repouso e nunca expostas em APIs de leitura.
/// Permite que cada tenant use suas próprias chaves OpenRouter/OpenAI/openai-compatible.
/// </summary>
public class AiProviderCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Global (Server), Client ou Site</summary>
    public AppApprovalScopeType ScopeType { get; set; }

    /// <summary>Preenchido quando ScopeType = Client</summary>
    public Guid? ClientId { get; set; }

    /// <summary>Preenchido quando ScopeType = Site</summary>
    public Guid? SiteId { get; set; }

    /// <summary>Provider: openai, openrouter, openai-compatible</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>URL base para chat/completions (opcional, usa default do provider se vazio)</summary>
    public string? BaseUrl { get; set; }

    /// <summary>URL base para embeddings (opcional, usa BaseUrl se vazio)</summary>
    public string? EmbeddingBaseUrl { get; set; }

    /// <summary>API Key criptografada para chat</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>API Key criptografada para embeddings</summary>
    public string? EmbeddingApiKeyEncrypted { get; set; }

    /// <summary>Hash do fingerprint da chave para cache/auditoria (SHA256 dos primeiros 4 + últimos 4 chars)</summary>
    public string? KeyFingerprintHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    // --- Helpers ---

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKeyEncrypted);

    public bool HasEmbeddingApiKey => !string.IsNullOrWhiteSpace(EmbeddingApiKeyEncrypted);
}
