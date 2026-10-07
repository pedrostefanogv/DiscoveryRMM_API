using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>Persistencia das inscricoes de Web Push por usuario.</summary>
public interface IPushSubscriptionRepository
{
    Task<IReadOnlyList<PushSubscription>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Insere ou atualiza pelo endpoint (chave natural). Se o endpoint ja existir
    /// para outro usuario, a inscricao e reapontada para o usuario atual — o
    /// navegador pertence a quem esta logado nele.
    /// </summary>
    Task<PushSubscription> UpsertAsync(PushSubscription subscription, CancellationToken ct = default);

    Task<bool> DeleteByEndpointAsync(Guid userId, string endpoint, CancellationToken ct = default);

    Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Remove as inscricoes mais antigas excedentes ao teto informado.</summary>
    Task<int> TrimAsync(Guid userId, int maxSubscriptions, CancellationToken ct = default);

    Task<int> CountByUserIdAsync(Guid userId, CancellationToken ct = default);
}
