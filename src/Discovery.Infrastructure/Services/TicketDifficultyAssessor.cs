using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Avaliador determinístico de dificuldade. A triagem por IA pode substituir o
/// nível pela estimativa do modelo, mas mantém esta versão como fallback.
/// </summary>
public class TicketDifficultyAssessor : ITicketDifficultyAssessor
{
    public Task<TicketDifficultyDto> AssessAsync(
        Ticket ticket,
        IReadOnlyList<TechnicianAffinityHit> similarTickets,
        CancellationToken ct = default)
    {
        var (level, rationale) = Services.Ai.TicketSignalExtractor.EstimateDifficulty(ticket, similarTickets);
        return Task.FromResult(new TicketDifficultyDto(level, "heuristic", rationale));
    }
}
