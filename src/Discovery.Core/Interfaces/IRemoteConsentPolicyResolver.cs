using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Resolve a política efetiva de consentimento de acesso remoto para uma
/// conexão de desktop: Servidor → Cliente → Site → Grupos → Usuário.
/// </summary>
public interface IRemoteConsentPolicyResolver
{
    Task<RemoteConsentPolicy> ResolveAsync(
        Guid userId,
        Guid clientId,
        Guid siteId,
        CancellationToken ct = default);
}
