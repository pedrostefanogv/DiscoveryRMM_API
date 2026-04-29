using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Repositório para credenciais de provedores AI por escopo.
/// </summary>
public interface IAiProviderCredentialRepository
{
    Task<AiProviderCredential?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Busca credencial exata por escopo + provider</summary>
    Task<AiProviderCredential?> GetByScopeAsync(Guid? clientId, Guid? siteId, string provider, CancellationToken ct = default);

    /// <summary>Busca TODAS as credenciais relevantes para a hierarquia (Global, Client, Site)</summary>
    Task<List<AiProviderCredential>> GetByHierarchyAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default);

    Task<List<AiProviderCredential>> GetAllAsync(CancellationToken ct = default);
    Task<AiProviderCredential> CreateAsync(AiProviderCredential credential, CancellationToken ct = default);
    Task<AiProviderCredential> UpdateAsync(AiProviderCredential credential, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
