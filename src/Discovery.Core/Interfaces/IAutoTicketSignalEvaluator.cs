using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Avalia o SINAL de um evento de monitoramento e decide se ele merece um
/// chamado. Determinístico, sem IA.
///
/// Responsabilidades: vínculo de sensor (abre/não abre), persistência
/// (magnitude × duração), flapping, manutenção e dependência (host não
/// alcançável). NÃO decide roteamento nem redige texto — isso é do
/// enriquecimento.
/// </summary>
public interface IAutoTicketSignalEvaluator
{
    /// <summary>
    /// Avalia o evento contra a política já resolvida para o escopo.
    /// </summary>
    /// <param name="monitoringEvent">Evento de monitoramento (opened/escalated/resolved).</param>
    /// <param name="policy">Política efetiva do cliente/site.</param>
    /// <param name="sensorKey">
    /// Chave do sensor (sem o prefixo "sensor:"), quando o evento veio de um
    /// sensor. Null para eventos de outras origens.
    /// </param>
    Task<AutoTicketSignalVerdict> EvaluateAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        string? sensorKey = null,
        CancellationToken cancellationToken = default);
}
