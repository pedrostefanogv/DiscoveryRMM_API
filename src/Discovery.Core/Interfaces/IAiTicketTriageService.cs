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

    /// <summary>Processa um lote da fila. Retorna a quantidade de chamados triados.</summary>
    Task<int> ProcessQueueBatchAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Rede de segurança: aplica fallback determinístico em chamados com
    /// estratégia AiTriage que continuam sem responsável após o tempo limite.
    /// </summary>
    Task<int> SweepUnassignedAsync(TimeSpan olderThan, int limit, CancellationToken ct = default);
}
