using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Resolve credenciais AI com herança hierárquica: Site → Client → Global (Server).
/// Se um Site tem credencial própria, usa ela. Senão, se o Client pai tem, usa a do Client.
/// Caso contrário, usa a Global (Server).
/// </summary>
public class AiCredentialResolver : IAiCredentialResolver
{
    private readonly IAiProviderCredentialRepository _credentialRepo;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<AiCredentialResolver> _logger;

    public AiCredentialResolver(
        IAiProviderCredentialRepository credentialRepo,
        ISecretProtector secretProtector,
        ILogger<AiCredentialResolver> logger)
    {
        _credentialRepo = credentialRepo;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    /// <summary>
    /// Resolve a credencial efetiva para um escopo.
    /// Ordem: Site → Client → Global.
    /// </summary>
    public async Task<ResolvedCredential?> ResolveAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
    {
        var allCredentials = await _credentialRepo.GetByHierarchyAsync(clientId, siteId, ct);

        // Ordena por precedência: Site (2) > Client (1) > Global (0)
        var ordered = allCredentials
            .OrderByDescending(c => (int)c.ScopeType)
            .ToList();

        // Pega a primeira que tenha API key (chat)
        var best = ordered.FirstOrDefault();

        if (best is null)
        {
            _logger.LogDebug("Nenhuma credencial AI encontrada para Client={ClientId} Site={SiteId}", clientId, siteId);
            return null;
        }

        _logger.LogDebug(
            "Credencial AI resolvida: Scope={ScopeType} Provider={Provider} para Client={ClientId} Site={SiteId}",
            best.ScopeType, best.Provider, clientId, siteId);

        return new ResolvedCredential
        {
            Provider = best.Provider,
            BaseUrl = best.BaseUrl,
            EmbeddingBaseUrl = best.EmbeddingBaseUrl,
            ApiKey = _secretProtector.UnprotectOrSelf(best.ApiKeyEncrypted),
            EmbeddingApiKey = _secretProtector.UnprotectOrSelf(best.EmbeddingApiKeyEncrypted),
            SourceScope = best.ScopeType,
            CredentialId = best.Id
        };
    }

    /// <summary>
    /// Resolve a chave de embedding efetiva.
    /// Pode ser de uma credencial diferente (EmbeddingApiKey) ou fallback para ApiKey.
    /// </summary>
    public async Task<string?> ResolveEmbeddingApiKeyAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(clientId, siteId, ct);
        return resolved?.EffectiveEmbeddingApiKey;
    }

    /// <summary>
    /// Resolve a chave de chat efetiva.
    /// </summary>
    public async Task<string?> ResolveChatApiKeyAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(clientId, siteId, ct);
        return resolved?.ApiKey;
    }
}
