using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação de ITicketAccessGuard.
///
/// A regra em si vive em <see cref="ITicketAccessPolicy"/> — este guard é apenas
/// a porta booleana usada pelos controllers de chat. Antes ele REIMPLEMENTAVA a
/// comparação de Cliente/Site, o que fazia a mesma decisão existir em dois lugares
/// e divergir com o tempo (o departamento do chamado nunca era considerado).
///
/// Mapeamento das ações:
/// - View/Delete   -> pode VER o chamado (escopo de cliente/site)
/// - Edit/Execute  -> pode TRATAR (membro do time do departamento)
/// </summary>
public class TicketAccessGuard(
    ITicketAccessPolicy accessPolicy,
    ITicketRepository ticketRepository,
    IScopeContext scopeContext) : ITicketAccessGuard
{
    public async Task<bool> CanAccessTicketAsync(
        Guid ticketId,
        ActionType action = ActionType.View,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId);
        if (ticket is null)
            return false;

        var userId = scopeContext.UserId;
        var isTreatAction = action is ActionType.Edit or ActionType.Execute;

        var decision = isTreatAction
            ? await accessPolicy.CanTreatAsync(userId, ticket, cancellationToken)
            : await accessPolicy.CanViewAsync(userId, ticket, cancellationToken);

        // Mesma semântica de shadow dos controllers: registra e concede durante a
        // fase de medição, para não bloquear o atendimento antes dos times estarem
        // populados.
        if (isTreatAction)
            decision = accessPolicy.ApplyShadow(decision, userId, ticketId, action.ToString());

        return decision.Allowed;
    }
}
