using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Valida o acesso do usuário autenticado a um chamado, considerando o escopo
/// (cliente/site) do token. Extraído de TicketsController para ser reusado por
/// controllers novos sem duplicar a regra.
/// </summary>
public interface ITicketAccessGuard
{
    Task<bool> CanAccessTicketAsync(Guid ticketId, ActionType action = ActionType.View, CancellationToken cancellationToken = default);
}
