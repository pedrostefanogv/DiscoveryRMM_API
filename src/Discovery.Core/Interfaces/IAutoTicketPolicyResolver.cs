using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Resolve a política de auto-ticket com herança hierárquica Server -> Client -> Site.
///
/// Reusa a mesma semântica do <c>IConfigurationResolver</c>: valores null são
/// preenchidos pelo nível superior. Adicionalmente respeita o
/// <c>LockedFieldsJson</c> de cada nível (campo travado no topo ignora
/// tentativa de override abaixo).
/// </summary>
public interface IAutoTicketPolicyResolver
{
    /// <summary>
    /// Resolve a política efetiva para um escopo. <paramref name="clientId"/> e
    /// <paramref name="siteId"/> nulos resolvem apenas o nível global.
    /// </summary>
    Task<ResolvedAutoTicketPolicy> ResolveAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lê o blob CRU de um nível específico (para a tela de configuração saber o
    /// que aquele nível sobrescreve, antes de resolver a herança).
    /// </summary>
    Task<string?> GetRawPolicyJsonAsync(
        string level,
        Guid? targetId = null,
        CancellationToken cancellationToken = default);
}
