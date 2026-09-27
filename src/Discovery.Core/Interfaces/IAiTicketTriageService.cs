using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Triagem por IA: monta candidatos da equipe do departamento, calcula o score
/// determinístico, consulta o modelo quando disponível e aplica (ou sugere) o
/// responsável, sempre de forma auditável e com fallback.
/// </summary>
public interface IAiTicketTriageService
{
    /// <summary>
    /// Avalia o chamado e registra a decisão sem anunciar: não grava atividade
    /// no histórico nem notifica. Usado pelo preview sob demanda, que pode ser
    /// disparado várias vezes pelo atendente.
    /// </summary>
    Task<TicketAssignmentResultDto> PreviewAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>Avalia e sugere o responsável sem alterar o chamado.</summary>
    Task<TicketAssignmentResultDto> RecommendAsync(
        Guid ticketId, Guid? triggeredByUserId = null, CancellationToken ct = default);

    /// <summary>Avalia e aplica o responsável conforme o modo do departamento.</summary>
    Task<TicketAssignmentResultDto> ApplyAsync(
        Guid ticketId, Guid? triggeredByUserId = null, CancellationToken ct = default);

    /// <summary>
    /// Ciclo periódico em lotes POR CLIENTE: respeita o vencimento do escopo
    /// (IntervalSeconds), a cota por cliente (fairness) e, no fim, aplica o
    /// fallback determinístico nos chamados que continuam sem responsável.
    /// </summary>
    Task<TriageCycleResult> ProcessDueAsync(CancellationToken ct = default);
}
