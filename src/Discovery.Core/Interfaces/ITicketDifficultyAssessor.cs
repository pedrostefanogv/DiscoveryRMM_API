using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Estima a dificuldade (1..5) de um chamado a partir de sinais do próprio
/// chamado e do histórico de chamados semelhantes. É determinístico e serve de
/// fallback quando a IA não está disponível.
/// </summary>
public interface ITicketDifficultyAssessor
{
    Task<TicketDifficultyDto> AssessAsync(
        Ticket ticket,
        IReadOnlyList<TechnicianAffinityHit> similarTickets,
        CancellationToken ct = default);
}
