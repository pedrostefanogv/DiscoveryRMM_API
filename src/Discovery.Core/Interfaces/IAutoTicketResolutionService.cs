using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Fecha o chamado correlacionado quando o sensor volta ao normal.
///
/// Corrige um bug latente documentado na pesquisa: o Zabbix só suprime
/// ESCALAÇÕES durante a manutenção, mas "recovery notifications are not sent if
/// the problem started during maintenance". Sem tratar isso, um alerta que abre
/// durante a janela de manutenção jamais é fechado — vira ticket órfão.
///
/// Também é o caminho que faz a resolução FECHAR em vez de abrir um chamado novo
/// (a ingestão já bloqueia criação em transition = "resolved").
/// </summary>
public interface IAutoTicketResolutionService
{
    /// <summary>
    /// Processa um evento de resolução: encontra o chamado correlacionado pelo
    /// DedupKey e o fecha, respeitando a política do escopo.
    /// </summary>
    /// <returns>O chamado fechado, ou null quando não havia nada a fechar.</returns>
    Task<Ticket?> CloseCorrelatedTicketAsync(
        AgentMonitoringEvent resolutionEvent,
        CancellationToken cancellationToken = default);
}
