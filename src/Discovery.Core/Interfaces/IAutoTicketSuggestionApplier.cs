using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Aplica (ou apenas registra) as sugestões da IA conforme a política.
///
/// Este é o único ponto que decide o que vira valor real do chamado. É código
/// NOSSO, com validação — nunca o LLM.
///
/// Regras invioláveis:
///  - Modo Suggest: registra e NÃO aplica.
///  - Modo AutoApply: aplica só se o campo está habilitado, a confiança atinge
///    o mínimo e a validação passa.
///  - PISO DE PRIORIDADE: em escalação, a IA nunca REBAIXA a prioridade; só eleva.
/// </summary>
public interface IAutoTicketSuggestionApplier
{
    /// <summary>
    /// Resolve os valores finais do chamado a partir do determinístico + sugestões.
    /// </summary>
    /// <param name="monitoringEvent">Evento de origem.</param>
    /// <param name="policy">Política efetiva do escopo.</param>
    /// <param name="enrichment">Resultado do enriquecimento (pode ser fallback).</param>
    /// <param name="deterministicPriority">Prioridade que a regra/sinal já determinou.</param>
    /// <param name="isEscalation">True quando o sinal decidiu Escalate (ativa o piso).</param>
    Task<AutoTicketAppliedSuggestion> ApplyAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        AutoTicketEnrichment enrichment,
        Enums.TicketPriority deterministicPriority,
        bool isEscalation,
        CancellationToken cancellationToken = default);
}
