namespace Discovery.Core.Interfaces;

/// <summary>Chamado semelhante já resolvido por um atendente (afinidade).</summary>
public sealed record TechnicianAffinityHit(
    Guid UserId,
    Guid TicketId,
    string? TicketTitle,
    double Similarity);

/// <summary>
/// Afinidade entre um chamado e chamados semelhantes já resolvidos, por
/// similaridade textual (pg_trgm). Degrada para lista vazia quando a extensão
/// não está disponível — a triagem continua, apenas sem este sinal.
/// </summary>
public interface ITechnicianAffinityRepository
{
    Task<IReadOnlyList<TechnicianAffinityHit>> FindSimilarResolvedAsync(
        string query,
        Guid? clientId,
        int limit,
        CancellationToken ct = default);
}
