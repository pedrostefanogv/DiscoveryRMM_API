namespace Discovery.Core.Interfaces;

/// <summary>
/// Lock global via Redis para download de artifacts P2P.
/// Evita que múltiplos grupos baixem o mesmo artifact da URL em paralelo.
/// </summary>
public interface IP2pLockService
{
    /// <summary>
    /// Tenta adquirir lock global para download do artifact.
    /// Retorna (acquired, holderToken). Se acquired=false, lock já existe.
    /// TTL padrão: 5 minutos.
    /// </summary>
    Task<(bool Acquired, string? HolderToken)> TryAcquireAsync(
        Guid clientId, Guid artifactId, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>
    /// Libera o lock global.
    /// </summary>
    Task ReleaseAsync(Guid clientId, Guid artifactId, CancellationToken ct = default);

    /// <summary>
    /// Verifica se existe lock ativo para o artifact.
    /// </summary>
    Task<bool> ExistsAsync(Guid clientId, Guid artifactId, CancellationToken ct = default);

    /// <summary>
    /// Renova o TTL do lock (chamado pelo fetcher a cada 90s).
    /// Retorna true se o lock ainda pertence a este holder.
    /// </summary>
    Task<bool> RenewAsync(Guid clientId, Guid artifactId, string holderToken, CancellationToken ct = default);
}
