using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;

namespace Discovery.Core.Interfaces.Auth;

/// <summary>
/// Resultado de uma decisão de acesso a um chamado, com o motivo legível.
/// O motivo existe para auditoria e para a UI explicar a negação (antes tudo
/// virava 404 genérico).
/// </summary>
public sealed record TicketAccessDecision(bool Allowed, string Reason, string? Code = null)
{
    public static TicketAccessDecision Permit(string reason) => new(true, reason);
    public static TicketAccessDecision Deny(string reason, string code) => new(false, reason, code);
}

/// <summary>
/// PONTO ÚNICO de autorização por chamado.
///
/// Antes desta abstração a regra estava DUPLICADA em
/// <c>TicketsController.CanAccessTicketAsync</c> e <c>TicketAccessGuard</c>, e ambas
/// comparavam apenas Cliente/Site — nunca o departamento nem a autoria do chamado.
///
/// Aqui as três decisões são separadas (ver / tratar / fechar), porque no modelo
/// anterior uma única permissão (<c>Tickets.Edit</c>) concedia as três:
///
/// - <b>Ver</b>    = ACL de Cliente/Site (comportamento legado preservado)
/// - <b>Tratar</b>  = acesso global OU membro do time do departamento do chamado
/// - <b>Fechar</b>  = pode tratar OU (é o solicitante E o destino é estado final)
///
/// O modo de observação (<see cref="ITicketAccessPolicy.ShadowMode"/>) permite medir
/// o impacto em produção antes de bloquear de fato: em shadow as negações de
/// tratar/fechar são REGISTRADAS mas não aplicadas.
/// </summary>
public interface ITicketAccessPolicy
{
    /// <summary>
    /// Quando true, negações de tratar/fechar são apenas logadas e o acesso é
    /// concedido (fase de medição). Ver e a ACL de Cliente/Site seguem valendo.
    /// </summary>
    bool ShadowMode { get; }

    /// <summary>O ator tem acesso global (bypassa todas as demais regras)?</summary>
    Task<bool> HasGlobalTicketAccessAsync(Guid? userId, CancellationToken ct = default);

    /// <summary>Pode VER o chamado (ACL de Cliente/Site).</summary>
    Task<TicketAccessDecision> CanViewAsync(Guid? userId, Ticket ticket, CancellationToken ct = default);

    /// <summary>Pode TRATAR/ATENDER o chamado (membro do time do departamento).</summary>
    Task<TicketAccessDecision> CanTreatAsync(Guid? userId, Ticket ticket, CancellationToken ct = default);

    /// <summary>
    /// Pode FECHAR o chamado a partir do estado informado. Regra: pode tratar, OU
    /// é o solicitante do próprio chamado E a transição leva a um estado final.
    /// </summary>
    Task<TicketAccessDecision> CanCloseAsync(Guid? userId, Ticket ticket, Guid targetStateId, CancellationToken ct = default);

    /// <summary>
    /// Pode EXECUTAR automação na máquina vinculada a este chamado (runbook).
    ///
    /// Exige DUAS condições, de propósito:
    /// 1. a permissão <c>Tickets:Execute</c> do usuário; E
    /// 2. o departamento do chamado estar habilitado a executar
    ///    (<see cref="Discovery.Core.Entities.Department.CanExecuteOnMachines"/>).
    ///
    /// Só o vínculo com o time NÃO basta: um departamento de compras pode ter um
    /// chamado vinculado a um agent sem que seu time deva rodar scripts no
    /// equipamento. Separar as duas condições é o que permite isso.
    /// </summary>
    Task<TicketAccessDecision> CanExecuteOnMachineAsync(Guid? userId, Ticket ticket, CancellationToken ct = default);

    /// <summary>Ids dos departamentos em que o usuário é membro ativo (para a listagem).</summary>
    Task<IReadOnlyList<Guid>> GetMemberDepartmentIdsAsync(Guid? userId, CancellationToken ct = default);

    /// <summary>
    /// Aplica o modo de observação: se a decisão é uma NEG de tratar/fechar e o
    /// shadow está ligado, registra a tentativa e devolve uma decisão PERMITIDA.
    /// Fora do shadow, devolve a decisão original intacta.
    ///
    /// Fica no contrato (e não só na implementação) porque todos os pontos de
    /// decisão precisam do mesmo comportamento — deixar cada chamador decidir
    /// como tratar o shadow garantiria divergência.
    /// </summary>
    TicketAccessDecision ApplyShadow(TicketAccessDecision decision, Guid? userId, Guid ticketId, string action);
}
