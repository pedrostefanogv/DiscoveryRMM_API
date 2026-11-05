using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Enriquece o chamado que JÁ foi decidido: redige título/descrição com contexto
/// real do equipamento e SUGERE prioridade, departamento e categoria.
///
/// Garantias de segurança:
///  - NÃO decide se o chamado abre (isso é do IAutoTicketSignalEvaluator).
///  - NÃO escreve nada: só lê dados e devolve um objeto.
///  - Usa CompleteAsync (sem tool calls) — a IA é fisicamente incapaz de agir.
///  - Qualquer falha/baixa confiança degrada para o texto determinístico.
///
/// Espelha como a indústria limita a IA: gate determinístico ANTES de qualquer
/// similaridade/LLM (Unified Alert Grouping do PagerDuty), e autonomia sempre
/// governada por aprovação/auditoria (LogicMonitor, Dynatrace).
/// </summary>
public interface IAutoTicketEnrichmentService
{
    /// <summary>
    /// Gera o enriquecimento para um evento, respeitando a política do escopo.
    /// Nunca lança: em qualquer erro devolve <c>AutoTicketEnrichment.Fallback</c>.
    /// </summary>
    /// <param name="monitoringEvent">Evento que gerou o chamado.</param>
    /// <param name="policy">Política efetiva (define se a IA está ligada e o que pode enviar).</param>
    Task<AutoTicketEnrichment> EnrichAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        CancellationToken cancellationToken = default);
}
