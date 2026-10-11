using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação de ITicketAccessGuard. Usuários com acesso global passam
/// direto; os demais só acessam chamados dentro do próprio ACL.
/// </summary>
public class TicketAccessGuard(
    ITicketQueryService ticketQueryService,
    IScopeContext scopeContext) : ITicketAccessGuard
{
    public async Task<bool> CanAccessTicketAsync(
        Guid ticketId,
        ActionType action = ActionType.View,
        CancellationToken cancellationToken = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Tickets, action);
        if (access.HasGlobalAccess)
            return true;

        var ticket = await ticketQueryService.GetTicketByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
            return false;

        return access.AllowedClientIds.Contains(ticket.ClientId)
            || (ticket.SiteId.HasValue && access.AllowedSiteIds.Contains(ticket.SiteId.Value));
    }
}
